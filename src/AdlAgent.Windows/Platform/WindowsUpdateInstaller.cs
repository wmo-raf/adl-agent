using System.Diagnostics;
using System.Globalization;
using AdlAgent.Core.Platform;
using AdlAgent.Core.Update;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace AdlAgent.Windows.Platform;

/// <summary>
/// Seam 5 on Windows: the two ways an install here can be replaced.
/// </summary>
/// <remarks>
/// Which one applies is not configured, it is observed. A machine installed
/// by the MSI is a Windows Service and knows it; a machine installed for a
/// technician without administrator rights is a Velopack install and knows
/// that. Anything else -- a folder somebody unzipped, a developer's
/// <c>dotnet run</c> -- is neither, and is left alone rather than having a
/// real install put down beside it.
/// <para>
/// The same observation answers which package ADL should offer, which is why
/// the tier is not a setting: a per-user install has no administrator rights
/// to run an MSI with, and a machine that had been told the wrong thing about
/// itself would fail every update for ever, quietly.
/// </para>
/// </remarks>
public sealed class WindowsUpdateInstaller : IUpdateInstaller
{
    /// <summary>
    /// How long to wait for Windows Installer to refuse a package before
    /// concluding that it has accepted it.
    /// </summary>
    /// <remarks>
    /// A refusal is immediate -- msiexec validates the package, finds the
    /// reason, writes its log and exits. An acceptance stops this service
    /// well before this elapses. Half a minute is comfortably past the first
    /// and comfortably short of anything a person is waiting on.
    /// </remarks>
    private static readonly TimeSpan RefusalWindow = TimeSpan.FromSeconds(30);

    private readonly ILogger<WindowsUpdateInstaller> _logger;
    private readonly Lazy<VelopackInstall?> _velopack;

    public WindowsUpdateInstaller(ILogger<WindowsUpdateInstaller> logger)
    {
        _logger = logger;

        // Lazily, and never fatally: asking Velopack about an install that is
        // not one of its own is a question with an answer, but asking it on a
        // machine it was never designed to run on (a Linux CI runner
        // resolving this graph, a developer's Mac) is not. Either way the
        // answer here is "this is not a Velopack install".
        _velopack = new Lazy<VelopackInstall?>(() =>
        {
            try
            {
                var locator = VelopackLocator.IsCurrentSet
                    ? VelopackLocator.Current
                    : VelopackLocator.CreateDefaultForPlatform(null, null);

                // The source is required by the constructor and never used:
                // what to install has already been decided by ADL, fetched by
                // the core, and proven against the hash ADL stated. All that
                // is wanted from Velopack is the swap itself.
                var manager = new UpdateManager(
                    new SimpleFileSource(new DirectoryInfo(AppContext.BaseDirectory)), null, locator);

                return manager.IsInstalled ? new VelopackInstall(manager, locator) : null;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    exception, "This install is not a Velopack install: {Message}", exception.Message);

                return null;
            }
        });
    }

    private VelopackInstall? Velopack => _velopack.Value;

    public string Tier => Velopack is not null ? UpdateTiers.User : UpdateTiers.Service;

    public bool CanApply => Velopack is not null || WindowsServiceHelpers.IsWindowsService();

    public async Task ApplyAsync(DownloadedUpdate update, CancellationToken cancellationToken = default)
    {
        switch (update.Kind)
        {
            case UpdateKinds.VelopackFull:
                await ApplyVelopackAsync(update).ConfigureAwait(false);

                return;

            case UpdateKinds.Msi:
                await ApplyWindowsInstallerAsync(update, cancellationToken).ConfigureAwait(false);

                return;

            default:
                throw new UpdateFailedException(
                    $"ADL served a '{update.Kind}' package, which this agent does not know how to install.");
        }
    }

    /// <summary>
    /// Hand the package to Windows Installer and let it stop this service.
    /// </summary>
    /// <remarks>
    /// A major upgrade, which is what the installer authoring declares: the
    /// service is stopped, the files under Program Files are replaced, and
    /// the service is started again -- by Windows Installer, not by anything
    /// here. Nothing this process could do after the call matters, because
    /// this process is one of the things being replaced.
    /// <para>
    /// What survives is everything under <c>%ProgramData%</c>: the device
    /// token, the configuration cache and the sweep log. The MSI does not own
    /// that directory -- the service creates it at runtime -- so an upgrade
    /// has nothing to say about it and a machine comes back paired
    /// (acceptance criterion: "self-updates without losing pairing or config
    /// cache").
    /// </para>
    /// <para>
    /// The log goes beside the package, under the state directory, because
    /// the alternative to a verbose log is a country server that came back
    /// on the old version for a reason nobody can reconstruct.
    /// </para>
    /// </remarks>
    private async Task ApplyWindowsInstallerAsync(
        DownloadedUpdate update, CancellationToken cancellationToken)
    {
        // Beside the package, whose directory is the one the core fetched it
        // into: resolving the state folder a second time here would be two
        // places to change and only one of them failing when they disagreed.
        var log = Path.Combine(
            Path.GetDirectoryName(update.Path) ?? ".",
            string.Create(CultureInfo.InvariantCulture, $"install-{update.Version}.log"));

        var start = new ProcessStartInfo("msiexec.exe")
        {
            ArgumentList = { "/i", update.Path, "/qn", "/norestart", "/l*v", log },
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _logger.LogInformation(
            "Handing {Package} to Windows Installer. This service is about to be stopped and replaced; " +
            "the installer's own log will be at {Log}.",
            update.Path, log);

        Process installing;

        try
        {
            installing = Process.Start(start)
                ?? throw new UpdateFailedException(
                    "Windows Installer did not start, and said nothing about why.");
        }
        catch (Exception exception) when (exception is not UpdateFailedException)
        {
            throw new UpdateFailedException(
                $"Could not start Windows Installer for {update.Path}: {exception.Message}", exception);
        }

        using (installing)
        {
            // Waited on, but not for long, and the asymmetry is the point.
            // An upgrade that is working stops this service as one of its
            // first acts, so the ordinary end of this method is not returning
            // at all. What the wait is for is the other ending: msiexec
            // refusing the package outright -- a downgrade, a package another
            // installation is blocking, a machine policy -- which it does in
            // seconds and which used to be indistinguishable from success.
            // A refusal that went unread was a machine that fetched the same
            // package every cycle for ever and never said why.
            using var refusalWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            refusalWindow.CancelAfter(RefusalWindow);

            try
            {
                await installing.WaitForExitAsync(refusalWindow.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Still going after all that: it is installing, and this
                // process is one of the things it is replacing.
                return;
            }

            if (!SucceededOrNeedsReboot(installing.ExitCode))
            {
                throw new UpdateFailedException(
                    $"Windows Installer refused the {update.Version} package with exit code " +
                    $"{installing.ExitCode}. Its log is at {log}.");
            }
        }
    }

    /// <summary>
    /// Whether an <c>msiexec</c> exit code means the install happened.
    /// </summary>
    /// <remarks>
    /// Two codes mean yes. <c>3010</c> is "installed, and Windows wants a
    /// reboot to finish", which for a service whose files were replaced under
    /// it is a normal outcome and not a failure to report; <c>1641</c> is the
    /// same thing where the installer has started that reboot itself.
    /// </remarks>
    private static bool SucceededOrNeedsReboot(int exitCode) =>
        exitCode is 0 or 3010 or 1641;

    /// <summary>
    /// Hand the package to Velopack and let it swap the install.
    /// </summary>
    /// <remarks>
    /// Velopack applies a package it finds in its own packages directory, so
    /// the verified file is copied there rather than applied where it landed.
    /// The copy is of bytes this agent has already hashed against what ADL
    /// stated, which is the only claim being made about them.
    /// </remarks>
    private async Task ApplyVelopackAsync(DownloadedUpdate update)
    {
        var install = Velopack
            ?? throw new UpdateFailedException(
                "ADL served a per-user package, but this is not a Velopack install.");

        try
        {
            // Velopack's own answer, and it can decline to give one: a
            // locator that cannot find the install it belongs to has nowhere
            // to stage a package.
            var packages = install.Locator.PackagesDir
                ?? throw new UpdateFailedException(
                    "This Velopack install does not say where its packages live, so the update cannot be staged.");

            Directory.CreateDirectory(packages);

            var staged = Path.Combine(packages, Path.GetFileName(update.Path));

            File.Copy(update.Path, staged, overwrite: true);

            var asset = await VelopackAsset.FromNupkgGenerateChecksumAsync(staged).ConfigureAwait(false);

            _logger.LogInformation(
                "Applying agent {Version} and restarting.", update.Version);

            install.Manager.ApplyUpdatesAndRestart(asset);
        }
        catch (Exception exception) when (exception is not UpdateFailedException)
        {
            throw new UpdateFailedException(
                $"Could not apply the per-user package for {update.Version}: {exception.Message}", exception);
        }
    }
}

/// <summary>A Velopack install, as this machine turned out to be one.</summary>
/// <remarks>
/// The locator is kept beside the manager because the swap needs both: the
/// manager performs it, and the locator says which directory Velopack will
/// look in for the package to perform it with.
/// </remarks>
internal sealed record VelopackInstall(UpdateManager Manager, IVelopackLocator Locator);
