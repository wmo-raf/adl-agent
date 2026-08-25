using System.Security.Principal;
using AdlAgent.Core;
using AdlAgent.Core.Platform;
using AdlAgent.Core.State;
using AdlAgent.Windows.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace AdlAgent.Windows;

/// <summary>
/// <c>adl-agent set-url &lt;url&gt; [--keep-pairing]</c>: the supported way to
/// change where a machine reports.
/// </summary>
/// <remarks>
/// Until this existed there was no way. The address is written to
/// <c>agent.ini</c> by the MSI and read once at start-up, so changing it meant
/// an administrator editing a file inside a directory whose permissions the
/// MSI has replaced with SYSTEM and Administrators, and then restarting the
/// service by hand -- three steps, none of them in the product, on a machine
/// nobody can reach.
/// <para>
/// Not a control-surface command. The pipe can pair the device and rebind a
/// station's folder, and any interactive logon session can reach it;
/// redirecting a machine's entire outbound path is a different order of thing
/// and belongs behind the operating system's own consent. So it is a verb on
/// the service executable, run elevated, and it does the whole job: validate,
/// stop, write, drop the pairing, start.
/// </para>
/// <para>
/// Deliberately usable without the tray. A machine with no desktop, or one
/// whose tray will not open, still has a supported way to be repointed.
/// </para>
/// </remarks>
public sealed class SetUrl
{
    /// <summary>The verb, as it is typed.</summary>
    public const string Verb = "set-url";

    /// <summary>The one switch: keep the token this machine already has.</summary>
    public const string KeepPairingSwitch = "--keep-pairing";

    private static readonly string Usage = string.Join(
        Environment.NewLine,
        "Usage: adl-agent set-url <https://adl.example.org> [--keep-pairing]",
        "",
        "  Points this machine at an ADL instance and restarts the service.",
        "  The device token is cleared, so the machine must be paired again;",
        "  --keep-pairing keeps it, for an instance that has only moved domain.");

    private readonly string _stateDirectory;
    private readonly IAgentStateStore _state;
    private readonly IServiceControl _service;
    private readonly bool _elevated;

    public SetUrl(
        string stateDirectory, IAgentStateStore state, IServiceControl service, bool elevated)
    {
        _stateDirectory = stateDirectory;
        _state = state;
        _service = service;
        _elevated = elevated;
    }

    /// <summary>True when these arguments are this verb.</summary>
    /// <remarks>
    /// The whole of the verb, arguments or not: <c>adl-agent set-url</c> with
    /// nothing after it must print how to use it, not start an agent host
    /// that would then run with the command line as configuration.
    /// </remarks>
    public static bool Handles(string[] args) =>
        args.Length > 0 && args[0] == Verb;

    /// <summary>This machine's settings, state and service.</summary>
    /// <remarks>
    /// The state store is taken from the head's own graph rather than composed
    /// again here, and that is not tidiness: <see cref="AgentOptions"/> lets a
    /// machine move its state directory, so a second store built on the
    /// default would report a pairing cleared while emptying a folder the
    /// service does not use. The graph is built and not run -- nothing starts,
    /// and the four loops are never constructed.
    /// <para>
    /// Built with no arguments on purpose. The host adds the command line as
    /// configuration, and this verb's arguments are not settings.
    /// </para>
    /// </remarks>
    public static SetUrl ForThisMachine()
    {
        var services = WindowsAgentHost.CreateBuilder([]).Build().Services;

        return new SetUrl(
            services.GetRequiredService<IHostLifecycle>().StateDirectory,
            services.GetRequiredService<IAgentStateStore>(),
            new WindowsServiceControl(),
            RunningElevated());
    }

    /// <summary>Run the verb. Returns the process exit code.</summary>
    public async Task<int> RunAsync(
        string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (!TryRead(args, out var url, out var keepPairing))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);

            return 2;
        }

        if (Refusal(url) is { } refusal)
        {
            await Say(output, refusal, "Nothing was changed.").ConfigureAwait(false);

            return 1;
        }

        var settingsFile = MachineSettings.PathIn(_stateDirectory);

        // Stopped before anything is touched, because the service owns these
        // files while it runs: it rewrites the configuration cache on every
        // sync and the token on a 401, so clearing them underneath it is a
        // race this verb would sometimes lose -- and the machine would come
        // back paired to an instance it is no longer pointed at.
        try
        {
            await _service.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceControlFailedException failure)
        {
            await Say(output, failure.Message, "Nothing was changed.").ConfigureAwait(false);

            return 1;
        }

        try
        {
            MachineSettings.PointAt(_stateDirectory, url);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Put the machine back the way it was found before saying so: it
            // was running when this started, and a failed repoint must not
            // also be an outage.
            var running = await StartedAgain(cancellationToken).ConfigureAwait(false);

            await Say(
                output,
                $"Could not write {settingsFile}: {exception.Message}",
                $"Nothing was changed. {running}")
                .ConfigureAwait(false);

            return 1;
        }

        string? pairingProblem = null;

        if (!keepPairing)
        {
            try
            {
                _state.ForgetInstance();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The address is what was asked for and it is written. A token
                // that outlived it is the case the agent already handles: ADL
                // refuses it and the machine says "re-pair needed".
                pairingProblem = exception.Message;
            }
        }

        var lines = new List<string>
        {
            $"ADL:      {url}",
            $"Written:  {settingsFile}",
            pairingProblem is not null
                ? $"Pairing:  could not be cleared -- {pairingProblem}"
                : keepPairing
                    ? "Pairing:  kept, at your request. If ADL refuses the token, pair this machine again."
                    : "Pairing:  cleared. Pair this machine again: adl-agent pair <code>",
        };

        if (pairingProblem is not null)
        {
            lines.Add(
                "          ADL will refuse the old token and the machine will ask to be paired again.");
        }

        // It is already elevated, so there is no reason to leave a
        // half-applied change and a sentence asking somebody to restart
        // something.
        try
        {
            await _service.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceControlFailedException failure)
        {
            lines.Add($"Service:  not started -- {failure.Message}");
            lines.Add(
                $"          The address is written. Start the '{WindowsAgentHost.ServiceName}' service "
                + "for it to take effect.");

            await output.WriteLineAsync(string.Join(Environment.NewLine, lines)).ConfigureAwait(false);

            return 1;
        }

        lines.Add("Service:  restarted, and reading the new address.");

        await output.WriteLineAsync(string.Join(Environment.NewLine, lines)).ConfigureAwait(false);

        return pairingProblem is null ? 0 : 1;
    }

    /// <summary>
    /// Why this machine cannot be pointed at <paramref name="url"/>, or
    /// <c>null</c> when it can.
    /// </summary>
    /// <remarks>
    /// The agent's own refusal, asked before anything is written or stopped. A
    /// verb that accepted what the service will refuse would produce a machine
    /// that installs cleanly and never reports, which is the failure mode this
    /// whole area exists to end.
    /// <para>
    /// The elevation check is here with it, and before it, for the same
    /// reason: a machine that is about to be told "no" should be told so
    /// before its service is stopped, and told which "no" it is. Without this
    /// the answer would be an access-denied on a path, which reads as a broken
    /// install rather than as a command run from the wrong window.
    /// </para>
    /// </remarks>
    private string? Refusal(string url)
    {
        if (!_elevated)
        {
            return "Changing where this machine reports needs administrator rights: the settings file is "
                + "in a folder only SYSTEM and Administrators may write, and the service has to be "
                + "restarted."
                + Environment.NewLine
                + "Run this again from an elevated command prompt.";
        }

        // The address before the machine, because a mistyped address is the
        // likelier of the two and is the one whoever ran this can fix where
        // they are standing.
        if (AgentOptions.ProblemWith(url) is { } problem)
        {
            return problem;
        }

        if (!Directory.Exists(_stateDirectory))
        {
            // Not created, on purpose: see MachineSettings.PointAt. A folder
            // made here would inherit permissions that let every local account
            // read the device token this machine is about to be given.
            return $"This machine has no ADL Agent state folder ({_stateDirectory}), so it is not an "
                + "installed agent. Install it with the MSI, which takes the address as ADLURL.";
        }

        return null;
    }

    /// <summary>
    /// The address and the switch, or <c>false</c> if this is not a usable
    /// command line.
    /// </summary>
    /// <remarks>
    /// An unrecognised switch is a usage error rather than something to
    /// ignore. <c>--keep-token</c> is the obvious thing to type for
    /// <c>--keep-pairing</c>, and quietly ignoring it would clear the pairing
    /// of every machine in a fleet somebody was moving domain.
    /// <para>
    /// An <em>empty</em> address is not a usage error, though: it is an
    /// address, and it gets the same refusal the agent gives a machine that
    /// has none. Only typing the verb with no address at all is somebody who
    /// wants to be told how to use it.
    /// </para>
    /// </remarks>
    private static bool TryRead(string[] args, out string url, out bool keepPairing)
    {
        url = "";
        keepPairing = false;

        var given = false;

        foreach (var argument in args.Skip(1))
        {
            if (argument.StartsWith('-'))
            {
                if (!argument.Equals(KeepPairingSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                keepPairing = true;

                continue;
            }

            if (given)
            {
                return false;
            }

            url = argument;
            given = true;
        }

        return given;
    }

    /// <summary>Start the service again after a failure, and say whether it came back.</summary>
    private async Task<string> StartedAgain(CancellationToken cancellationToken)
    {
        try
        {
            await _service.StartAsync(cancellationToken).ConfigureAwait(false);

            return $"The '{WindowsAgentHost.ServiceName}' service is running again.";
        }
        catch (ServiceControlFailedException failure)
        {
            return $"The '{WindowsAgentHost.ServiceName}' service is stopped and did not start: "
                + failure.Message;
        }
    }

    private static Task Say(TextWriter output, params string[] lines) =>
        output.WriteLineAsync(string.Join(Environment.NewLine, lines));

    /// <summary>Whether this process is running as an administrator.</summary>
    /// <remarks>
    /// True off Windows, where there is no such thing as the elevation this
    /// verb needs and the directories it writes are a developer's own. The
    /// check is here rather than behind a seam of its own because there is
    /// nothing to choose between implementations of it -- the tests state the
    /// answer directly.
    /// </remarks>
    private static bool RunningElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        using var identity = WindowsIdentity.GetCurrent();

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
