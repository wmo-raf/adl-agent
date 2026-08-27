using AdlAgent.Core.Api;

namespace AdlAgent.Core.Heartbeat;

/// <summary>
/// One unit's pass, as the store is told about it.
/// </summary>
/// <remarks>
/// <see cref="Completed"/> is the whole of why this is a record and not a
/// pair of arguments. A unit cut short by an ADL that stopped answering still
/// knows what it scanned and why it stopped, and that sentence is the only
/// thing standing between an operator and a station reading "no cycle yet"
/// for ever -- but it must not move the completion mark, because a machine
/// whose every pass is cut short is exactly the machine ADL is meant to call
/// stuck.
/// </remarks>
public sealed record CycleUnitReport
{
    public required DateTimeOffset At { get; init; }

    /// <summary>False when the pass was cut short: its counts stand, its completion does not.</summary>
    public required bool Completed { get; init; }

    public required IReadOnlyList<CycleLinkReport> Links { get; init; }

    /// <summary>What each of this unit's stations has that ADL does not.</summary>
    public required IReadOnlyDictionary<long, int> Backlogs { get; init; }
}

/// <summary>
/// What this machine's collection has lately come to, held in memory for the
/// heartbeat to read.
/// </summary>
/// <remarks>
/// In memory and not on disk, because the fact it holds is about this run:
/// after a restart the honest answer to "when did a pass last finish" is "not
/// since I started", and ADL's own cycle-stuck check reads it that way.
/// <para>
/// A rolling picture per station rather than a snapshot of one pass over the
/// whole machine. That is the difference wmo-raf/adl#304 makes: collection
/// happens a unit at a time -- a station and whatever it shares a folder with
/// -- and each unit finishes on its own. A store that kept only the last
/// whole-machine pass would have nothing to say about a station until every
/// other folder on the box had also been walked, which on a machine working
/// through a backlog is hours.
/// </para>
/// <para>
/// So each unit overwrites its own stations and leaves the rest alone, and
/// what the heartbeat sends is every station's latest word. The completion
/// stamp is the most recent unit that actually finished.
/// </para>
/// </remarks>
public sealed class CycleReportStore : ICycleReportSource
{
    private readonly Lock _gate = new();

    private readonly Dictionary<long, Station> _stations = [];

    private DateTimeOffset? _completedAt;

    /// <summary>Every station's latest word, and when a pass last finished.</summary>
    /// <remarks>
    /// Null until some unit has run to its end. A machine whose every pass so
    /// far was cut short has counts to show -- they are in
    /// <see cref="_stations"/> and they reach the operator through the
    /// station list -- but it has not completed anything, and saying
    /// otherwise to ADL would spend the one alarm that catches a machine
    /// whose link never stays up long enough to finish.
    /// </remarks>
    public CycleReport? LastCompletedCycle
    {
        get
        {
            lock (_gate)
            {
                if (_completedAt is null)
                {
                    return null;
                }

                return new CycleReport
                {
                    CompletedAt = _completedAt,
                    // Ordered by station so that two heartbeats a minute
                    // apart describe the fleet in the same order, however the
                    // units they came from were scheduled.
                    Links = _stations
                        .OrderBy(entry => entry.Key)
                        .Select(entry => entry.Value.Report)
                        .ToList(),
                };
            }
        }
    }

    /// <summary>
    /// What this machine holds that ADL does not, across every station.
    /// </summary>
    /// <remarks>
    /// Summed over the latest word from each station rather than over one
    /// pass, for the same reason the links are: on a machine collecting a
    /// unit at a time there is no single pass that has seen them all.
    /// </remarks>
    public int? BacklogCount
    {
        get
        {
            lock (_gate)
            {
                return _stations.Count == 0
                    ? null
                    : _stations.Values.Sum(station => station.Backlog);
            }
        }
    }

    /// <summary>When this station's own pass last finished, or null.</summary>
    /// <remarks>
    /// Read by the station list, to decide whether a collect somebody asked
    /// for at the machine has been overtaken by a scheduled pass. Per station
    /// because that is the only honest comparison now: the machine's most
    /// recent finish says nothing about whether *this* station has been round
    /// again since the button was pressed.
    /// </remarks>
    public DateTimeOffset? LastPassAt(long stationLinkId)
    {
        lock (_gate)
        {
            return _stations.TryGetValue(stationLinkId, out var station)
                ? station.At
                : null;
        }
    }

    /// <summary>Take one unit's pass.</summary>
    public void Record(CycleUnitReport unit)
    {
        lock (_gate)
        {
            foreach (var link in unit.Links)
            {
                _stations[link.StationLinkId] = new Station(
                    link,
                    unit.Backlogs.TryGetValue(link.StationLinkId, out var backlog) ? backlog : 0,
                    unit.At);
            }

            if (unit.Completed)
            {
                _completedAt = unit.At;
            }
        }
    }

    /// <summary>
    /// Note that a tick went round everything this machine has.
    /// </summary>
    /// <remarks>
    /// The units stamp their own completion as they finish, so on an ordinary
    /// tick this agrees with the last of them and adds nothing. It earns its
    /// place on the machine that has <em>no</em> units: every station
    /// switched off in ADL, or none linked yet. Such a machine completes a
    /// pass over an empty fleet every check interval and is perfectly
    /// healthy, and without this it would report having never finished
    /// anything -- which ADL reads, correctly by its own rules, as a machine
    /// whose work has stopped.
    /// </remarks>
    public void Finished(DateTimeOffset at)
    {
        lock (_gate)
        {
            _completedAt = at;
        }
    }

    /// <summary>
    /// Forget the stations this machine no longer has.
    /// </summary>
    /// <remarks>
    /// Without it the picture would be a record of every station this device
    /// has ever been given, and a machine whose stations were moved elsewhere
    /// months ago would go on reporting their last counts and their backlog
    /// to ADL for the life of the service.
    /// </remarks>
    public void Prune(IReadOnlySet<long> known)
    {
        lock (_gate)
        {
            foreach (var stationLinkId in _stations.Keys.Where(id => !known.Contains(id)).ToList())
            {
                _stations.Remove(stationLinkId);
            }
        }
    }

    /// <summary>One station's latest word, and when it was said.</summary>
    private readonly record struct Station(CycleLinkReport Report, int Backlog, DateTimeOffset At);
}
