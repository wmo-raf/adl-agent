using AdlAgent.Core.Configuration;
using AdlAgent.Core.Heartbeat;
using AdlAgent.Core.Hosting;
using AdlAgent.Core.Pairing;
using AdlAgent.Core.Platform;
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
    private readonly IUpdateInstaller _installer;
    private readonly IHostLifecycle _host;
    private readonly AgentOptions _options;

    public AgentStatusReader(
        AgentSession session,
        ConfigurationService configuration,
        HeartbeatMonitor heartbeat,
        AgentCadence cadence,
        UpdateService updates,
        IUpdateInstaller installer,
        IHostLifecycle host,
        IOptions<AgentOptions> options)
    {
        _session = session;
        _configuration = configuration;
        _heartbeat = heartbeat;
        _cadence = cadence;
        _updates = updates;
        _installer = installer;
        _host = host;
        _options = options.Value;
    }

    /// <summary>
    /// What the person standing at an unconfigured machine can actually do,
    /// which is not the same on both tiers.
    /// </summary>
    /// <remarks>
    /// The service tier's answer needs an administrator: the settings file
    /// sits in a directory whose permissions the MSI replaced with SYSTEM and
    /// Administrators, and the service has to be restarted to re-read it.
    /// (wmo-raf/adl#292 turns that into one verb, and #295 into a button; the
    /// sentence should name them once they exist.)
    /// <para>
    /// The per-user tier has no installer property to have been given and no
    /// elevation available to the technician it exists for, so its answer is
    /// an environment variable read at the next logon. That is a command line
    /// on the one tier whose whole reason for existing is somebody who should
    /// not need one -- a knowing trade, written down here and in the README's
    /// known gaps rather than discovered on a country server.
    /// </para>
    /// </remarks>
    private string DescribeTheFix()
    {
        if (_installer.Tier == UpdateTiers.User)
        {
            return "Set it for your account, then sign out and back in: "
                + "setx Agent__AdlBaseUrl https://your-adl.example.org";
        }

        var file = _host.SettingsFilePath;

        return file is null
            ? "An administrator must set Agent:AdlBaseUrl on this machine and restart the agent."
            : $"An administrator must set AdlBaseUrl under [Agent] in {file}, then restart the ADL Agent service.";
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

        // Asked, not thrown. This is the read the tray makes every second on
        // exactly the machine whose address is wrong.
        var problem = _options.DescribeConfigurationProblem();

        return new AgentStatusSnapshot
        {
            AgentVersion = Core.AgentVersion.Current,
            AdlUrl = _options.AdlBaseUrl,
            Configured = problem is null,
            ConfigurationProblem = problem,
            ConfigurationHint = problem is null ? null : DescribeTheFix(),
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
