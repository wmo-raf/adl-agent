using AdlAgent.Core.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Configuration;

/// <summary>
/// Re-read this device's configuration from ADL on the check interval.
/// </summary>
/// <remarks>
/// The cycle this loop keeps is the scan cycle's: sync is what a cycle starts
/// with, and when the scan work lands it will run from here rather than
/// beside it. Until then this is the loop that makes central changes
/// propagate and keeps the offline cache warm -- so a machine paired today
/// and reconfigured from HQ tomorrow follows, with nobody in-country
/// involved.
/// <para>
/// Separate from the heartbeat loop and staying separate: this one talks to
/// folders and will get slow and stuck, and that is exactly the condition the
/// heartbeat has to survive to report.
/// </para>
/// </remarks>
public sealed class ConfigurationSyncLoop : BackgroundService
{
    private readonly ConfigurationService _configuration;
    private readonly AgentCadence _cadence;
    private readonly AgentWakeSignal _wake;
    private readonly TimeProvider _time;
    private readonly ILogger<ConfigurationSyncLoop> _logger;

    public ConfigurationSyncLoop(
        ConfigurationService configuration,
        AgentCadence cadence,
        AgentWakeSignal wake,
        TimeProvider time,
        ILogger<ConfigurationSyncLoop> logger)
    {
        _configuration = configuration;
        _cadence = cadence;
        _wake = wake;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await SyncAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await _wake.WaitAsync(_cadence.CheckInterval, _time, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass. Never throws, for the same reason the heartbeat does not.</summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var configuration = await _configuration.RefreshAsync(cancellationToken)
                .ConfigureAwait(false);

            if (configuration is not null && !configuration.FromCache)
            {
                _cadence.Adopt(
                    configuration.Sync.Device.HeartbeatIntervalMinutes,
                    configuration.Sync.Device.CheckIntervalMinutes);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not refresh the configuration.");
        }
    }
}
