using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdlAgent.Core.Hosting;

/// <summary>
/// Do a thing, wait, do it again -- and never die of it.
/// </summary>
/// <remarks>
/// The agent's loops all have this shape and all have to survive the same
/// conditions: a link that comes and goes, a folder that is not there, an ADL
/// that answers badly. The shape is shared; the failures are not. Each loop
/// keeps its own interval, its own errors and its own last-completed state,
/// so a wedged scan cycle still leaves the heartbeat beating -- which is the
/// distinction the whole monitoring story rests on.
/// <para>
/// What they do share is having nowhere to send: a machine with no ADL
/// address is not a machine whose calls fail, it is a machine with no call to
/// make. The check is here rather than in each loop so that a fourth one
/// cannot be written without it -- and it is deliberately not in
/// <c>AgentControlService</c>, which is a plain <c>BackgroundService</c> and
/// must go on answering, because the pipe is how the tray says any of this to
/// the person standing there.
/// </para>
/// </remarks>
public abstract class AgentLoop : BackgroundService
{
    private readonly AgentWakeSignal _wake;
    private readonly TimeProvider _time;
    private readonly AgentOptions _options;
    private readonly ILogger _logger;

    protected AgentLoop(
        AgentWakeSignal wake,
        TimeProvider time,
        IOptions<AgentOptions> options,
        ILogger logger)
    {
        _wake = wake;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>How long to wait between passes. Read fresh each time, because ADL may have moved it.</summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>
    /// One pass. Implementations must not throw: a loop that dies leaves a
    /// machine that looks like it is working and is not.
    /// </summary>
    protected abstract Task RunOnceAsync(CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nowhere to send. Returning rather than looping on purpose: the
        // address cannot change under a running process -- the settings file
        // is read once at start-up (reloadOnChange is off, deliberately, see
        // MachineSettings), and the environment is taken at logon -- so a
        // loop that woke every few minutes to find the same empty setting
        // would only be writing the same line into a log nobody is reading.
        // Whatever sets the address restarts the agent, and this runs again.
        //
        // Said once, at Warning, and then silence. The state is reported
        // continuously over the control surface instead, which is where
        // somebody is actually looking.
        var problem = _options.DescribeConfigurationProblem();

        if (problem is not null)
        {
            _logger.LogWarning(
                "{Loop} is not running: {Problem} The agent stays up and reports itself as not configured.",
                GetType().Name,
                problem);

            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Before the pass, not after. A wake that arrives while this loop
            // is working is meant for the sleep that follows -- and taking
            // the signal only afterwards would take the fresh one Set() left
            // behind, so the loop would sleep through the very nudge somebody
            // is standing there watching for.
            var listening = _wake.Listen();

            await RunOnceAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await _wake.WaitAsync(listening, Interval, _time, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
