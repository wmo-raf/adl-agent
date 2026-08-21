using Microsoft.Extensions.Hosting;

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
/// </remarks>
public abstract class AgentLoop : BackgroundService
{
    private readonly AgentWakeSignal _wake;
    private readonly TimeProvider _time;

    protected AgentLoop(AgentWakeSignal wake, TimeProvider time)
    {
        _wake = wake;
        _time = time;
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
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await _wake.WaitAsync(Interval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
