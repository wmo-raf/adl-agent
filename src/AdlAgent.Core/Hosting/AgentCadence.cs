using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Hosting;

/// <summary>
/// How often this machine talks to ADL -- ADL's numbers, not the agent's.
/// </summary>
/// <remarks>
/// Both cadences are served on every sync and every heartbeat, so a
/// deployment can change how closely it watches its fleet, or how hard its
/// machines work their folders, without anyone touching an install in the
/// field. They are two numbers and not one on purpose: the heartbeat loop and
/// the scan loop are separate precisely so that a wedged scan still
/// heartbeats, and a shared cadence would quietly re-couple them.
/// <para>
/// Whatever arrives is clamped. A machine that believed a zero would spin
/// against its ADL instance as fast as the link allows, and the failure mode
/// of a bad number reaching a fleet is worse than the failure mode of
/// ignoring it.
/// </para>
/// </remarks>
public sealed class AgentCadence
{
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan Shortest = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Longest = TimeSpan.FromHours(24);

    private readonly ILogger<AgentCadence> _logger;
    private readonly Lock _gate = new();

    private TimeSpan _heartbeat = DefaultHeartbeatInterval;
    private TimeSpan _check = DefaultCheckInterval;

    public AgentCadence(ILogger<AgentCadence> logger)
    {
        _logger = logger;
    }

    public TimeSpan HeartbeatInterval
    {
        get
        {
            lock (_gate)
            {
                return _heartbeat;
            }
        }
    }

    public TimeSpan CheckInterval
    {
        get
        {
            lock (_gate)
            {
                return _check;
            }
        }
    }

    /// <summary>Take the cadences from an answer ADL just gave.</summary>
    public void Adopt(int? heartbeatMinutes, int? checkMinutes)
    {
        var heartbeat = Clamp(heartbeatMinutes, DefaultHeartbeatInterval);
        var check = Clamp(checkMinutes, DefaultCheckInterval);

        bool changed;

        lock (_gate)
        {
            changed = heartbeat != _heartbeat || check != _check;
            _heartbeat = heartbeat;
            _check = check;
        }

        if (changed)
        {
            _logger.LogInformation(
                "ADL set the cadences: heartbeat every {Heartbeat}, scan every {Check}.",
                heartbeat, check);
        }
    }

    private TimeSpan Clamp(int? minutes, TimeSpan fallback)
    {
        if (minutes is null or <= 0)
        {
            return fallback;
        }

        var asked = TimeSpan.FromMinutes(minutes.Value);

        if (asked < Shortest || asked > Longest)
        {
            _logger.LogWarning(
                "ADL asked for an interval of {Asked} minutes, which is outside {Shortest}-{Longest}; keeping {Fallback}.",
                minutes, Shortest, Longest, fallback);

            return fallback;
        }

        return asked;
    }
}
