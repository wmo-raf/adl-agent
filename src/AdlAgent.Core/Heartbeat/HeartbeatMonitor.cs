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
    private string? _lastError;

    /// <summary>When a beat was last tried, whether or not it arrived.</summary>
    public DateTimeOffset? LastAttemptAt => Read(() => _lastAttemptAt);

    /// <summary>When ADL last answered a beat.</summary>
    public DateTimeOffset? LastSuccessAt => Read(() => _lastSuccessAt);

    /// <summary>ADL's own word for this device at that moment: online, degraded, offline.</summary>
    public string? FleetStatus => Read(() => _fleetStatus);

    /// <summary>
    /// How far this machine's clock is from ADL's, as ADL measured it. Worth
    /// showing locally: the file windows this agent runs on are measured
    /// against this clock, so a skew is a quiet data-loss risk and the person
    /// standing here is the only one who can fix it.
    /// </summary>
    public int? ClockSkewSeconds => Read(() => _clockSkewSeconds);

    /// <summary>Why the last beat did not arrive, if it did not.</summary>
    public string? LastError => Read(() => _lastError);

    public void RecordSuccess(HeartbeatResponse response, DateTimeOffset at)
    {
        lock (_gate)
        {
            _lastAttemptAt = at;
            _lastSuccessAt = at;
            _fleetStatus = response.Status;
            _clockSkewSeconds = response.ClockSkewSeconds;
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

    private T Read<T>(Func<T> read)
    {
        lock (_gate)
        {
            return read();
        }
    }
}
