using System.Collections.Generic;
using System.Linq;
using AdlAgent.Core.Status;

namespace AdlAgent.Tray;

/// <summary>
/// How a set of stations is doing, in the one order this window judges them.
/// </summary>
/// <remarks>
/// The ladder is: ignore what HQ has switched off, then a folder that has not
/// been bound, then a station that collected nothing, then nothing to do. It
/// is asked twice over -- once of every station on the machine, to write the
/// line at the top of the window, and once of each connection's own stations,
/// to write that connection's row in the list -- and it is one function here
/// rather than two implementations because the two answers appear on screen
/// together. A connection row reading "3 need a folder" beside a line reading
/// "nothing to do" is not a cosmetic disagreement; it is the window telling
/// a technician two different things about the same machine.
/// <para>
/// It holds the stations it judged rather than only a verdict, because both
/// callers name one of them -- "Bind a folder to Kakamega" -- and re-finding
/// it afterwards would be the same filter written a third time.
/// </para>
/// </remarks>
public sealed record StationStanding
{
    /// <summary>Which rung this set is on.</summary>
    public required StandingKind Kind { get; init; }

    /// <summary>Every station judged, switched off or not.</summary>
    public required int Total { get; init; }

    /// <summary>Those HQ has switched on -- the only ones the rungs below consider.</summary>
    public required IReadOnlyList<AgentStationSnapshot> Live { get; init; }

    /// <summary>Live stations with no local folder, so nothing can be found for them.</summary>
    public required IReadOnlyList<AgentStationSnapshot> Unbound { get; init; }

    /// <summary>Live stations that said something went wrong last cycle.</summary>
    public required IReadOnlyList<AgentStationSnapshot> Failing { get; init; }

    /// <summary>The colour this standing carries, wherever it is drawn.</summary>
    /// <remarks>
    /// Here rather than at each call site so the dot beside a connection and
    /// the band behind the next-step line are the same decision. Both of the
    /// settled states are green: a machine with every station switched off in
    /// ADL has nothing wrong with it, and an amber row would send a
    /// technician looking for a fault that is an administrator's deliberate
    /// choice.
    /// </remarks>
    public TrayState Attention => Kind switch
    {
        StandingKind.BindAFolder or StandingKind.FixAStation => TrayState.NeedsAttention,
        _ => TrayState.Working,
    };

    /// <summary>
    /// Judge a set of stations.
    /// </summary>
    /// <remarks>
    /// Switched-off stations are excluded before anything else is asked. A
    /// disabled station is an administrator's decision rather than anything a
    /// technician standing at this machine can act on, and a line telling
    /// them to act on it would be a line that never goes away.
    /// </remarks>
    public static StationStanding Of(IReadOnlyList<AgentStationSnapshot> stations)
    {
        var live = stations.Where(station => station.Enabled).ToList();

        var unbound = live
            .Where(station => string.IsNullOrWhiteSpace(station.Config.LocalFolderPath))
            .ToList();

        var failing = live
            .Where(station => !string.IsNullOrEmpty(station.Error))
            .ToList();

        var kind = stations.Count switch
        {
            0 => StandingKind.NoStations,
            _ when unbound.Count > 0 => StandingKind.BindAFolder,
            _ when failing.Count > 0 => StandingKind.FixAStation,
            _ when live.Count == 0 => StandingKind.AllSwitchedOff,
            _ => StandingKind.Collecting,
        };

        return new StationStanding
        {
            Kind = kind,
            Total = stations.Count,
            Live = live,
            Unbound = unbound,
            Failing = failing,
        };
    }
}

/// <summary>The rungs, in the order they are decided.</summary>
public enum StandingKind
{
    /// <summary>ADL has linked no stations here at all.</summary>
    NoStations,

    /// <summary>A station has no local folder, so nothing can be found for it.</summary>
    BindAFolder,

    /// <summary>A station collected nothing last cycle and said why.</summary>
    FixAStation,

    /// <summary>Stations are linked, and HQ has switched every one of them off.</summary>
    AllSwitchedOff,

    /// <summary>Everything a person could do has been done.</summary>
    Collecting,
}
