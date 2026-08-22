using AdlAgent.Core.Configuration;
using AdlAgent.Core.Heartbeat;
using AdlAgent.Core.Hosting;
using AdlAgent.Core.Pairing;
using AdlAgent.Core.Update;
using Microsoft.Extensions.Options;

namespace AdlAgent.Core.Status;

/// <summary>Assembles the one answer the control surface hands the local UI.</summary>
public sealed class AgentStatusReader
{
    private readonly AgentSession _session;
    private readonly ConfigurationService _configuration;
    private readonly HeartbeatMonitor _heartbeat;
    private readonly AgentCadence _cadence;
    private readonly UpdateService _updates;
    private readonly AgentOptions _options;

    public AgentStatusReader(
        AgentSession session,
        ConfigurationService configuration,
        HeartbeatMonitor heartbeat,
        AgentCadence cadence,
        UpdateService updates,
        IOptions<AgentOptions> options)
    {
        _session = session;
        _configuration = configuration;
        _heartbeat = heartbeat;
        _cadence = cadence;
        _updates = updates;
        _options = options.Value;
    }

    public AgentStatusSnapshot Read()
    {
        // Three reads, each internally consistent: what this machine's
        // standing is, what it is working from, and how its reporting is
        // going. Assembling the tray's picture from loose properties is what
        // would let it show a machine as unpaired beside the name of the
        // device it is paired to.
        var session = _session.Snapshot();
        var configuration = _configuration.Snapshot();
        var heartbeat = _heartbeat.Snapshot();
        var update = _updates.Last;

        return new AgentStatusSnapshot
        {
            AgentVersion = Core.AgentVersion.Current,
            AdlUrl = _options.AdlBaseUrl,
            PairingState = session.State.ToString(),
            RePairNeeded = session.State == Pairing.PairingState.RePairNeeded,
            DeviceId = session.Device?.Id,
            DeviceName = session.Device?.Name,
            PairedAt = session.PairedAt,
            LastSyncedAt = configuration.LastSyncedAt,
            ConfigFromCache = configuration.Configuration?.FromCache ?? false,
            ConfigVersion = configuration.Configuration?.Version,
            StationLinkCount = configuration.Configuration?.StationLinks.Count() ?? 0,
            LastHeartbeatAt = heartbeat.LastSuccessAt,
            FleetStatus = heartbeat.FleetStatus,
            ClockSkewSeconds = heartbeat.ClockSkewSeconds,
            CheckIntervalMinutes = (int)_cadence.CheckInterval.TotalMinutes,
            HeartbeatIntervalMinutes = (int)_cadence.HeartbeatInterval.TotalMinutes,
            LastError = heartbeat.LastError,
            UpdateState = update.Outcome.ToString(),
            UpdateVersion = update.OfferedVersion,
            UpdatePinned = update.Pinned,
            UpdateDetail = update.Detail,
            UpdateCheckedAt = update.At == default ? null : update.At,
        };
    }
}
