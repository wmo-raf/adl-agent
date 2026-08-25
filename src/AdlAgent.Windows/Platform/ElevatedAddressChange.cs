using System.ComponentModel;
using System.Diagnostics;

namespace AdlAgent.Windows.Platform;

/// <summary>
/// <c>adl-agent set-url</c>, started with the <c>runas</c> verb.
/// </summary>
/// <remarks>
/// The whole of the elevation: <see cref="ProcessStartInfo.UseShellExecute"/>
/// with <c>runas</c> is how one process asks Windows to start another
/// elevated, and Windows -- not this program -- raises the consent prompt,
/// checks the password, and decides. There is no code path here that runs the
/// change without it.
/// <para>
/// What this cannot do is read the verb's output. <c>runas</c> requires
/// <c>UseShellExecute</c>, which forbids redirecting the standard streams, so
/// the answer is an exit code and nothing else. That is why the sentence for
/// a failure names the command to run in a window where its own words can be
/// read, rather than inventing a reason for it.
/// </para>
/// <para>
/// It is deliberately the same command line a technician types. The verb is
/// the whole product here; this is the shortest wire between a button and it.
/// </para>
/// </remarks>
public sealed class ElevatedAddressChange : IAddressChange
{
    /// <summary>
    /// <c>ERROR_CANCELLED</c>: what ShellExecute returns when the consent
    /// prompt is dismissed, or an administrator's password is not given.
    /// </summary>
    private const int ConsentRefused = 1223;

    /// <summary>
    /// How long to wait for the verb before giving up on it.
    /// </summary>
    /// <remarks>
    /// It stops and starts a Windows service, each of which this agent allows
    /// ninety seconds; and before either of them somebody has to answer a
    /// prompt. Long enough for all three, and bounded, because the window
    /// asking is the one a technician is watching.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(5);

    private readonly string _agent;

    /// <param name="agentExecutable">
    /// The service executable. Beside the tray by default, which is where the
    /// MSI puts both of them; named by a test, which has neither.
    /// </param>
    public ElevatedAddressChange(string? agentExecutable = null) =>
        _agent = agentExecutable ?? Path.Combine(AppContext.BaseDirectory, "adl-agent.exe");

    /// <summary>
    /// The command line, exactly as somebody would type it.
    /// </summary>
    /// <remarks>
    /// Built from <see cref="SetUrl"/>'s own constants rather than from
    /// strings written here, so that renaming the verb or the switch breaks
    /// the build instead of the button.
    /// </remarks>
    public static string[] ArgumentsFor(string adlBaseUrl, bool keepPairing) =>
        keepPairing
            ? [SetUrl.Verb, adlBaseUrl, SetUrl.KeepPairingSwitch]
            : [SetUrl.Verb, adlBaseUrl];

    public async Task<AddressChange> RequestAsync(
        string adlBaseUrl, bool keepPairing, CancellationToken cancellationToken = default)
    {
        var arguments = ArgumentsFor(adlBaseUrl, keepPairing);

        if (!File.Exists(_agent))
        {
            // A tray running from somewhere the service is not: a developer's
            // build, or an install somebody has moved half of. Said plainly,
            // because the alternative is a consent prompt for a file that
            // does not exist.
            return new AddressChange(
                AddressChangeOutcome.Refused,
                $"This window could not find the ADL Agent program ({_agent}), so it cannot change "
                + "the address. From an elevated command prompt: "
                + $"adl-agent {string.Join(' ', arguments)}");
        }

        var start = new ProcessStartInfo(_agent)
        {
            // Both required for the verb below: ShellExecute is what knows
            // how to elevate, and it is also what makes the standard streams
            // unreadable. Hidden rather than CreateNoWindow, which
            // ShellExecute ignores -- otherwise a console flashes up on the
            // technician's screen and vanishes.
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process? running;

        try
        {
            running = Process.Start(start);
        }
        catch (Win32Exception refused) when (refused.NativeErrorCode == ConsentRefused)
        {
            return new AddressChange(
                AddressChangeOutcome.Declined,
                "Windows was not given permission to change this machine's address.");
        }
        catch (Exception exception)
        {
            return new AddressChange(
                AddressChangeOutcome.Refused,
                $"Windows would not start the change: {exception.Message}");
        }

        if (running is null)
        {
            return new AddressChange(
                AddressChangeOutcome.Refused,
                "Windows did not start the change, and said nothing about why.");
        }

        using (running)
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            waiting.CancelAfter(Patience);

            try
            {
                await running.WaitForExitAsync(waiting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new AddressChange(
                    AddressChangeOutcome.Refused,
                    "The change is still running after "
                    + $"{Patience.TotalMinutes:F0} minutes. This window has stopped waiting for it; "
                    + "the Status tab will say where this machine reports once it finishes.");
            }

            if (running.ExitCode == 0)
            {
                return new AddressChange(AddressChangeOutcome.Changed, "");
            }

            // The exit code and nothing else, because there is nothing else:
            // see the remarks above. What is worth saying is where the reason
            // can be read.
            return new AddressChange(
                AddressChangeOutcome.Refused,
                $"The change did not finish (adl-agent set-url exited with {running.ExitCode}). "
                + "Run it from an elevated command prompt to see what it says: "
                + $"adl-agent {string.Join(' ', arguments)}");
        }
    }
}
