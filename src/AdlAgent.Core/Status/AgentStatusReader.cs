using AdlAgent.Core.Configuration;
using AdlAgent.Core.Heartbeat;
using AdlAgent.Core.Hosting;
using AdlAgent.Core.Pairing;
using Microsoft.Extensions.Options;

namespace AdlAgent.Core.Status;

/// <summary>Assembles the one answer the control surface hands the local UI.</summary>
public sealed class AgentStatusReader
{
    private readonly AgentSession _session;
    private readonly ConfigurationService _configuration;
    private readonly HeartbeatMonitor _heartbeat;
    private readonly AgentCadence _cadence;
    private readonly AgentOptions _options;

    public AgentStatusReader(
        AgentSession session,
        ConfigurationService configuration,
        HeartbeatMonitor heartbeat,
        AgentCadence cadence,
        IOptions<AgentOptions> options)
    {
        _session = session;
        _configuration = configuration;
        _heartbeat = heartbeat;
        _cadence = cadence;
        _options = options.Value;
    }

    public AgentStatusSnapshot Read()
    {
        var device = _session.Device;
        var configuration = _configuration.Current;
        var state = _session.State;

        return new AgentStatusSnapshot
        {
            AgentVersion = Core.AgentVersion.Current,
            AdlUrl = _options.AdlBaseUrl,
            PairingState = state.ToString(),
            RePairNeeded = state == Pairing.PairingState.RePairNeeded,
            DeviceId = device?.Id,
            DeviceName = device?.Name,
            PairedAt = _session.PairedAt,
            LastSyncedAt = _configuration.LastSyncedAt,
            ConfigFromCache = configuration?.FromCache ?? false,
            ConfigVersion = configuration?.Version,
            StationLinkCount = configuration?.StationLinks.Count() ?? 0,
            LastHeartbeatAt = _heartbeat.LastSuccessAt,
            FleetStatus = _heartbeat.FleetStatus,
            ClockSkewSeconds = _heartbeat.ClockSkewSeconds,
            CheckIntervalMinutes = (int)_cadence.CheckInterval.TotalMinutes,
            HeartbeatIntervalMinutes = (int)_cadence.HeartbeatInterval.TotalMinutes,
            LastError = _heartbeat.LastError,
        };
    }
}
