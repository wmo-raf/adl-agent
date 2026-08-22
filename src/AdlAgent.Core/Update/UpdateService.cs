using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using AdlAgent.Core.Api;
using AdlAgent.Core.Pairing;
using AdlAgent.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdlAgent.Core.Update;

/// <summary>
/// Ask ADL what this machine should be running, and make it so.
/// </summary>
/// <remarks>
/// Three rules, and the order they are applied in is the whole design.
/// <list type="number">
/// <item>
/// <description>
/// <b>ADL decides what is on offer.</b> The pin is enforced server-side, so
/// a pinned machine is never even told a newer release exists (story 29). An
/// agent that had to remember not to update would be an agent that one day
/// forgets.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Only forwards.</b> A package is applied only when it is newer than
/// what is running. A pin below the running version holds the machine where
/// it is rather than rolling it back: Windows Installer refuses a downgrade
/// anyway, and a fleet that could be walked backwards from a text field is a
/// worse thing to own than one that has to be uninstalled by hand.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Bytes are proven before they are run.</b> The SHA-256 is checked
/// against what ADL said before the package is handed to anything that can
/// execute it, and a package that fails is deleted and not fetched again
/// until the offer changes. Pilots ship unsigned (decision #262), so this
/// check is the only thing standing between a bad download and the binary
/// that runs as LocalSystem.
/// </description>
/// </item>
/// </list>
/// <para>
/// Nothing here throws. An update is the least urgent thing the agent does,
/// and every failure in it must cost exactly nothing to the cycle that ships
/// a country's observations.
/// </para>
/// </remarks>
public sealed class UpdateService
{
    /// <summary>Where packages land, under the state directory.</summary>
    private const string DownloadFolderName = "updates";

    /// <summary>
    /// What a fetched package is called, and therefore what may be deleted
    /// from that directory: anything else in there was written by whatever
    /// installed the last update, and is somebody's only account of it.
    /// </summary>
    private const string PackagePrefix = "adl-agent-";

    private readonly IAdlApiClient _client;
    private readonly AgentSession _session;
    private readonly IUpdateInstaller _installer;
    private readonly AgentOptions _options;
    private readonly IHostLifecycle _host;
    private readonly TimeProvider _time;
    private readonly ILogger<UpdateService> _logger;
    private readonly Lock _gate = new();

    /// <summary>
    /// A version whose package did not hash to what ADL promised.
    /// </summary>
    /// <remarks>
    /// Remembered so that a corrupt release is fetched once rather than
    /// every cycle for ever -- forty megabytes every ten minutes down a
    /// country link, to fail the same check each time. In memory only: a
    /// restart re-fetching it once is harmless, and the alternative is a
    /// file on disk that outlives the fix.
    /// </remarks>
    private string? _refusedVersion;

    private bool _saidThereIsNoFeed;

    private UpdateReport _last = UpdateReport.None;

    public UpdateService(
        IAdlApiClient client,
        AgentSession session,
        IUpdateInstaller installer,
        IOptions<AgentOptions> options,
        IHostLifecycle host,
        TimeProvider time,
        ILogger<UpdateService> logger)
    {
        _client = client;
        _session = session;
        _installer = installer;
        _options = options.Value;
        _host = host;
        _time = time;
        _logger = logger;
    }

    /// <summary>The last check, for the status the tray draws.</summary>
    public UpdateReport Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>
    /// One check. Never throws.
    /// </summary>
    public async Task<UpdateReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return Record(await DecideAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The update check failed.");

            return Record(Report(UpdateOutcome.Failed, null, false, exception.Message));
        }
    }

    private async Task<UpdateReport> DecideAsync(CancellationToken cancellationToken)
    {
        var token = _session.ActiveToken;

        if (token is null)
        {
            // An unpaired machine has nobody to ask, and a revoked one has
            // been told to stop talking. Neither is a failed check.
            return Report(UpdateOutcome.NotPaired, null, false,
                "This machine is not paired with ADL, so there is nothing to ask about updates.");
        }

        UpdateOffer offer;

        try
        {
            offer = await _client.UpdateOfferAsync(token, _installer.Tier, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DeviceRevokedException)
        {
            _session.MarkRevoked();

            return Report(UpdateOutcome.NotPaired, null, false,
                "ADL no longer accepts this device's token.");
        }
        catch (AdlRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // An ADL instance whose agent plugin predates the update feed.
            // Said once and then left alone: it is a fact about the server,
            // it will not change until somebody upgrades it, and repeating it
            // every cycle would bury everything else in the log.
            if (!_saidThereIsNoFeed)
            {
                _saidThereIsNoFeed = true;

                _logger.LogInformation(
                    "This ADL instance serves no agent update feed, so this machine will not update itself.");
            }

            return Report(UpdateOutcome.NoFeed, null, false,
                "This ADL instance serves no agent update feed.");
        }
        catch (AdlRequestException exception)
        {
            _logger.LogWarning("ADL refused the update check: {Detail}", exception.Detail);

            return Report(UpdateOutcome.Failed, null, false, exception.Detail);
        }
        catch (AdlUnreachableException exception)
        {
            // The normal condition on these links, and the least important
            // call the agent makes. Debug, not warning: the heartbeat and the
            // cycle already say the link is down, loudly.
            _logger.LogDebug("Could not ask ADL about updates: {Message}", exception.Message);

            return Report(UpdateOutcome.Unreachable, null, false, exception.Message);
        }

        return await ActOnAsync(token, offer, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateReport> ActOnAsync(
        string token, UpdateOffer offer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(offer.Version))
        {
            return Report(UpdateOutcome.NothingOffered, null, offer.Pinned,
                Sentence(offer, "ADL has no agent release for this machine."));
        }

        if (!ReleaseVersion.TryParse(offer.Version, out var offered))
        {
            return Report(UpdateOutcome.Failed, offer.Version, offer.Pinned,
                $"ADL offered version '{offer.Version}', which is not a version number this agent can read.");
        }

        if (!ReleaseVersion.TryParse(AgentVersion.Current, out var running))
        {
            // Cannot happen from a release build, and if it ever did the safe
            // reading is "do not touch this machine": an agent that cannot
            // say what it is would be installing over itself blind.
            return Report(UpdateOutcome.Failed, offer.Version, offer.Pinned,
                $"This agent calls itself '{AgentVersion.Current}', which it cannot compare with what ADL offers.");
        }

        if (offered <= running)
        {
            if (offer.Pinned && offered < running)
            {
                _logger.LogInformation(
                    "ADL pins this machine to {Pinned}; it is running {Running} and will stay there.",
                    offered, running);

                return Report(UpdateOutcome.Held, offer.Version, true,
                    $"Pinned to {offered}. This machine runs {running} and stays there; " +
                    "an agent is never rolled back by itself.");
            }

            return Report(UpdateOutcome.UpToDate, offer.Version, offer.Pinned,
                $"Running {running}, which is what ADL offers.");
        }

        if (offer.Artifact is null)
        {
            return Report(UpdateOutcome.Failed, offer.Version, offer.Pinned,
                $"ADL offers {offered} but served no {_installer.Tier}-tier package for it.");
        }

        if (WasRefused(offer.Version))
        {
            return Report(UpdateOutcome.Failed, offer.Version, offer.Pinned,
                $"The {offered} package this instance serves did not match the hash ADL stated. " +
                "It will not be fetched again until ADL offers something else.");
        }

        if (!_options.AutoUpdate)
        {
            _logger.LogInformation(
                "ADL offers {Offered} and this machine runs {Running}; automatic updates are switched off here.",
                offered, running);

            return Report(UpdateOutcome.Available, offer.Version, offer.Pinned,
                $"Version {offered} is available. Automatic updates are switched off on this machine.");
        }

        if (!_installer.CanApply)
        {
            _logger.LogInformation(
                "ADL offers {Offered} and this machine runs {Running}, but this install is not one the agent can replace.",
                offered, running);

            return Report(UpdateOutcome.Available, offer.Version, offer.Pinned,
                $"Version {offered} is available. This copy of the agent was not installed by an " +
                "installer it can upgrade, so it will not replace itself.");
        }

        return await FetchAndApplyAsync(token, offer, offered, running, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<UpdateReport> FetchAndApplyAsync(
        string token,
        UpdateOffer offer,
        ReleaseVersion offered,
        ReleaseVersion running,
        CancellationToken cancellationToken)
    {
        var artifact = offer.Artifact!;
        var destination = Path.Combine(
            PrepareDownloadDirectory(FileNameFor(offered, artifact)),
            FileNameFor(offered, artifact));

        // The package may already be here, from a cycle whose install did not
        // take: msiexec refused it, or the machine lost power between the
        // download and the swap. Fetching it again would be tens of megabytes
        // down a country link to arrive at the same bytes, every cycle, for as
        // long as the condition lasts -- so what is already proven is reused,
        // and only what cannot be proven is fetched.
        if (await AlreadyHeldAsync(destination, artifact, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "The {Offered} package is already here and still matches what ADL says it is; " +
                "installing it again without fetching it.",
                offered);

            return await InstallAsync(offer, offered, artifact, destination, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "ADL offers agent {Offered}; this machine runs {Running}. Fetching {Bytes} bytes.",
            offered, running, artifact.Size);

        try
        {
            await _client.DownloadUpdateAsync(
                token, artifact.Path, destination, artifact.Size, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DeviceRevokedException)
        {
            _session.MarkRevoked();
            Discard(destination);

            return Report(UpdateOutcome.NotPaired, offer.Version, offer.Pinned,
                "ADL no longer accepts this device's token.");
        }
        catch (Exception exception) when (exception is AdlRequestException or AdlUnreachableException or IOException)
        {
            // A part-written package is worse than none: the next cycle
            // would find a file of the right name and the wrong length.
            Discard(destination);

            _logger.LogWarning(
                "Could not fetch the {Offered} package: {Message}", offered, exception.Message);

            return Report(UpdateOutcome.Failed, offer.Version, offer.Pinned,
                $"Could not fetch the {offered} package: {exception.Message}");
        }

        var actual = await Sha256Async(destination, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(actual, artifact.Sha256?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            // The whole point of the check. Deleted rather than kept for
            // inspection: what is on disk is an executable that arrived
            // pretending to be something else.
            Discard(destination);

            lock (_gate)
            {
                _refusedVersion = offer.Version;
            }

            _logger.LogError(
                "The {Offered} package did not match the hash ADL stated ({Expected}); it hashed to {Actual}. " +
                "It has been deleted and this machine stays on {Running}.",
                offered, artifact.Sha256, actual, running);

            return Report(UpdateOutcome.Failed, offer.Version, offer.Pinned,
                $"The {offered} package did not match the hash ADL stated. It was deleted and not installed.");
        }

        return await InstallAsync(offer, offered, artifact, destination, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<UpdateReport> InstallAsync(
        UpdateOffer offer,
        ReleaseVersion offered,
        UpdateArtifact artifact,
        string package,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "The {Offered} package is what ADL said it is. Installing; this service is about to be replaced.",
            offered);

        try
        {
            await _installer
                .ApplyAsync(new DownloadedUpdate(offered.ToString(), artifact.Kind, package), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UpdateFailedException exception)
        {
            // The package stays on disk. It is proven, and the next cycle
            // will try it again without paying for it again -- which is the
            // difference between a machine that retries and a machine that
            // re-downloads.
            _logger.LogError(exception, "The {Offered} package could not be installed.", offered);

            return Report(UpdateOutcome.Failed, offer.Version, offer.Pinned,
                $"The {offered} package was fetched and verified, but could not be installed: {exception.Message}");
        }

        return Report(UpdateOutcome.Applying, offer.Version, offer.Pinned,
            $"Installing version {offered}.");
    }

    /// <summary>
    /// Whether the package is already on disk and still what ADL describes.
    /// </summary>
    /// <remarks>
    /// Re-hashed rather than taken on trust from its own presence: what is
    /// being decided is whether to run a file as an installer, and a
    /// part-written download from a cycle that lost power looks exactly like
    /// a finished one from here.
    /// </remarks>
    private async Task<bool> AlreadyHeldAsync(
        string path, UpdateArtifact artifact, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != artifact.Size)
            {
                return false;
            }

            var actual = await Sha256Async(path, cancellationToken).ConfigureAwait(false);

            return string.Equals(actual, artifact.Sha256?.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Where a package is fetched to, with every other one gone.
    /// </summary>
    /// <remarks>
    /// Cleared rather than accumulated. The service tier's installer is
    /// handed a path and this process is stopped moments later, so nothing
    /// here ever gets the chance to tidy up after a successful update: the
    /// next check does it instead. On a machine whose disk is measured in
    /// gigabytes, two forgotten installers is a real fraction of it.
    /// <para>
    /// Only the packages, though. The platform installer writes its own log
    /// into this directory, and that log is the only account anybody gets of
    /// an update that went wrong on a machine nobody can reach -- deleting it
    /// ten minutes later, from the check that goes looking for what to do
    /// instead, would destroy the evidence of the thing being investigated.
    /// </para>
    /// </remarks>
    private string PrepareDownloadDirectory(string keep)
    {
        var root = string.IsNullOrWhiteSpace(_options.StateDirectory)
            ? _host.StateDirectory
            : _options.StateDirectory!;

        var directory = Path.Combine(root, DownloadFolderName);

        Directory.CreateDirectory(directory);

        foreach (var stale in Directory.EnumerateFiles(directory, $"{PackagePrefix}*"))
        {
            // Everything except the one about to be used: a package left by
            // an install that did not take is the whole point of coming back
            // here, and deleting it on the way to reusing it would be a fine
            // way to download it for ever.
            if (!string.Equals(
                    Path.GetFileName(stale), keep, StringComparison.OrdinalIgnoreCase))
            {
                Discard(stale);
            }
        }

        return directory;
    }

    /// <summary>
    /// What to call the package on disk.
    /// </summary>
    /// <remarks>
    /// Built here rather than taken from the answer. The name decides what
    /// Windows Installer will agree to look at, and a name that came off the
    /// wire could carry a directory separator with it -- a file path this
    /// process would then write to as LocalSystem. The version and the kind
    /// are both already known here, and they are all a name needs to say.
    /// </remarks>
    private static string FileNameFor(ReleaseVersion version, UpdateArtifact artifact) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{PackagePrefix}{version}{UpdateKinds.ExtensionFor(artifact.Kind)}");

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);

        var digest = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexStringLower(digest);
    }

    private void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Could not delete {Path}: {Message}", path, exception.Message);
        }
    }

    private bool WasRefused(string version)
    {
        lock (_gate)
        {
            return string.Equals(_refusedVersion, version, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Sentence(UpdateOffer offer, string fallback) =>
        string.IsNullOrWhiteSpace(offer.Reason) ? fallback : offer.Reason;

    private UpdateReport Report(UpdateOutcome outcome, string? version, bool pinned, string detail) =>
        new(_time.GetUtcNow(), outcome, version, pinned, detail);

    private UpdateReport Record(UpdateReport report)
    {
        lock (_gate)
        {
            _last = report;
        }

        return report;
    }
}
