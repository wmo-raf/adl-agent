using AdlAgent.Core.Api;

namespace AdlAgent.Core.Heartbeat;

/// <summary>
/// How the last few heartbeats went, for whoever is standing at the machine.
/// </summary>
/// <remarks>
/// ADL keeps its own view of this device's liveness and is the authority on
/// it. This is the other side of the same conversation: what the machine
/// itself can say without asking anyone. It is what the tray reads, and it is
/// what makes a link failure diagnosable in the building rather than only
/// from HQ.
/// </remarks>
public sealed class HeartbeatMonitor
{
    private readonly Lock _gate = new();

    private DateTimeOffset? _lastAttemptAt;
    private DateTimeOffset? _lastSuccessAt;
    private string? _fleetStatus;
    private int? _clockSkewSeconds;
    private int? _reconciliationIntervalHours;
    private string? _lastError;

    /// <summary>How the last beat went, read at one instant.</summary>
    public HeartbeatSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new HeartbeatSnapshot(
                _lastAttemptAt, _lastSuccessAt, _fleetStatus, _clockSkewSeconds,
                _reconciliationIntervalHours, _lastError);
        }
    }

    /// <summary>When a beat was last tried, whether or not it arrived.</summary>
    public DateTimeOffset? LastAttemptAt => Snapshot().LastAttemptAt;

    /// <summary>When ADL last answered a beat.</summary>
    public DateTimeOffset? LastSuccessAt => Snapshot().LastSuccessAt;

    /// <summary>ADL's own word for this device at that moment: online, degraded, offline.</summary>
    public string? FleetStatus => Snapshot().FleetStatus;

    /// <summary>
    /// How far this machine's clock is from ADL's, as ADL measured it. Worth
    /// showing locally: the file windows this agent runs on are measured
    /// against this clock, so a skew is a quiet data-loss risk and the person
    /// standing here is the only one who can fix it.
    /// </summary>
    public int? ClockSkewSeconds => Snapshot().ClockSkewSeconds;

    /// <summary>
    /// How often ADL last said a station should offer its whole folder.
    /// <c>null</c> when no beat has been answered, or when the ADL answering
    /// predates the setting.
    /// </summary>
    /// <remarks>
    /// From the beat rather than the sync response, and it is the one number
    /// here that could have come from either. A deployment-wide setting moves
    /// no <c>config_version</c>, so nothing in a sync response ever says the
    /// number is new -- and the beat is the more frequent of the two calls,
    /// so it is the sooner of the two to notice.
    /// </remarks>
    public int? ReconciliationIntervalHours => Snapshot().ReconciliationIntervalHours;

    /// <summary>Why the last beat did not arrive, if it did not.</summary>
    public string? LastError => Snapshot().LastError;

    public void RecordSuccess(HeartbeatResponse response, DateTimeOffset at)
    {
        lock (_gate)
        {
            _lastAttemptAt = at;
            _lastSuccessAt = at;
            _fleetStatus = response.Status;
            _clockSkewSeconds = response.ClockSkewSeconds;
            _reconciliationIntervalHours = response.ReconciliationIntervalHours;
            _lastError = null;
        }
    }

    public void RecordFailure(string error, DateTimeOffset at)
    {
        lock (_gate)
        {
            _lastAttemptAt = at;
            _lastError = error;
        }
    }
}

/// <summary>How the machine's own reporting is going, as of one instant.</summary>
public sealed record HeartbeatSnapshot(
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    string? FleetStatus,
    int? ClockSkewSeconds,
    int? ReconciliationIntervalHours,
    string? LastError);
