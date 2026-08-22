using AdlAgent.Core.Hosting;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Update;

/// <summary>
/// Ask ADL what this machine should be running, on the cycle's cadence.
/// </summary>
/// <remarks>
/// Its own loop rather than a step at the end of the scan cycle, for the
/// reason every loop here is its own loop: an update check that hung on a
/// slow download would be a scan cycle that stopped shipping a country's
/// observations, and the two have nothing to say to each other. It runs on
/// the check interval because that is the rhythm the spec sets for noticing
/// things -- a fleet on a ten-minute cadence learns about a release within
/// ten minutes -- and because a second cadence to configure would be a
/// second cadence to get wrong.
/// <para>
/// Nothing here throws, and the service below it does not either. An update
/// is the least urgent thing this agent does and must never be able to cost
/// it anything else.
/// </para>
/// </remarks>
public sealed class UpdateLoop : AgentLoop
{
    private readonly UpdateService _updates;
    private readonly AgentCadence _cadence;
    private readonly ILogger<UpdateLoop> _logger;

    public UpdateLoop(
        UpdateService updates,
        AgentCadence cadence,
        AgentWakeSignal wake,
        TimeProvider time,
        ILogger<UpdateLoop> logger)
        : base(wake, time)
    {
        _updates = updates;
        _cadence = cadence;
        _logger = logger;
    }

    protected override TimeSpan Interval => _cadence.CheckInterval;

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _updates.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "The update check failed.");
        }
    }
}
