using System.Diagnostics;

namespace AdlAgent.Windows.Platform;

/// <summary>
/// The Service Control Manager, through <c>net.exe</c>.
/// </summary>
/// <remarks>
/// <c>net stop</c> and <c>net start</c> rather than <c>sc.exe</c> because
/// they wait: <c>sc</c> signals the Service Control Manager and returns at
/// once, so a caller using it has to poll <c>sc query</c> and read a state
/// out of its printed output before it may say the service is back. These two
/// block until the transition has happened and then exit with whether it did,
/// which is the whole of what is wanted here -- and the waiting is the point,
/// because what happens between the stop and the start is a file the stopped
/// service must not be holding.
/// </remarks>
public sealed class WindowsServiceControl : IServiceControl
{
    /// <summary>
    /// How long either half may take. Generous, because a service stopping in
    /// the middle of an upload over a country link finishes what it is doing
    /// first, and bounded, because a person is standing at this machine
    /// waiting for the command to come back.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // "/y" so that a service with dependents does not stop at a
        // confirmation prompt nobody is at a console to answer. The outcome
        // is not checked: see IServiceControl.StopAsync.
        await RunAsync(["stop", WindowsAgentHost.ServiceName, "/y"], cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var started = await RunAsync(["start", WindowsAgentHost.ServiceName], cancellationToken)
            .ConfigureAwait(false);

        if (started.ExitCode == 0)
        {
            return;
        }

        throw new ServiceControlFailedException(
            Reason(started) is { Length: > 0 } why
                ? why
                : $"net start \"{WindowsAgentHost.ServiceName}\" exited with {started.ExitCode}.");
    }

    private static async Task<CommandOutcome> RunAsync(
        string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("net.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process running;

        try
        {
            running = Process.Start(start)
                ?? throw new ServiceControlFailedException(
                    "net.exe did not start, and said nothing about why.");
        }
        catch (Exception exception) when (exception is not ServiceControlFailedException)
        {
            throw new ServiceControlFailedException(
                $"Could not run net.exe to control the {WindowsAgentHost.ServiceName} service: "
                + exception.Message,
                exception);
        }

        using (running)
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            waiting.CancelAfter(Patience);

            // Read both pipes while waiting rather than after: a command that
            // fills one of them and is never drained blocks for ever, and
            // this one is being waited on with a stopwatch running.
            var output = running.StandardOutput.ReadToEndAsync(waiting.Token);
            var error = running.StandardError.ReadToEndAsync(waiting.Token);

            try
            {
                await running.WaitForExitAsync(waiting.Token).ConfigureAwait(false);

                return new CommandOutcome(
                    running.ExitCode,
                    await output.ConfigureAwait(false),
                    await error.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ServiceControlFailedException(
                    $"net {string.Join(' ', arguments)} did not finish within "
                    + $"{Patience.TotalSeconds:F0} seconds.");
            }
        }
    }

    /// <summary>What <c>net</c> said, as one line, or empty if it said nothing.</summary>
    private static string Reason(CommandOutcome outcome) =>
        string.Join(
            " ",
            (outcome.Error + Environment.NewLine + outcome.Output)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.Length > 0));

    private readonly record struct CommandOutcome(int ExitCode, string Output, string Error);
}
