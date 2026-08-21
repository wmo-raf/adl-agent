using AdlAgent.Core.Hosting;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Cycle;

/// <summary>
/// Run the upload cycle on the check interval ADL set.
/// </summary>
/// <remarks>
/// Separate from the heartbeat loop and staying separate: this one talks to
/// folders and to a link that comes and goes, so it is the loop that will get
/// slow and stuck -- and "the machine is up and its work has stopped" is
/// exactly the observation the heartbeat exists to make. They share the
/// cadence object and nothing else.
/// <para>
/// Nothing here throws. A loop that died would leave a machine that looks
/// like it is working and is not, which is the failure mode that made
/// reverse SSH tunnels unbearable in the first place.
/// </para>
/// </remarks>
public sealed class UploadCycleLoop : AgentLoop
{
    private readonly UploadCycle _cycle;
    private readonly AgentCadence _cadence;
    private readonly ILogger<UploadCycleLoop> _logger;

    public UploadCycleLoop(
        UploadCycle cycle,
        AgentCadence cadence,
        AgentWakeSignal wake,
        TimeProvider time,
        ILogger<UploadCycleLoop> logger)
        : base(wake, time)
    {
        _cycle = cycle;
        _cadence = cadence;
        _logger = logger;
    }

    protected override TimeSpan Interval => _cadence.CheckInterval;

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _cycle.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "The upload cycle failed.");
        }
    }
}
