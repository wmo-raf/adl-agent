using AdlAgent.Core.Api;
using AdlAgent.Core.Diagnostics;

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
/// <para>
/// Beside that picture, and not instead of it, is a bounded queue of the
/// passes themselves. The picture answers "how is this machine now", which is
/// what the liveness ladder is counted in; the queue answers "what has this
/// station been doing for a fortnight", which the picture cannot, because
/// every beat overwrites it. Both travel on the same beat
/// (wmo-raf/adl#307).
/// </para>
/// </remarks>
public sealed class CycleReportStore : ICycleReportSource
{
    private readonly Lock _gate = new();

    private readonly Dictionary<long, Station> _stations = [];

    /// <summary>Passes that have finished and not yet been accepted by ADL.</summary>
    private readonly Queue<CyclePassReport> _passes = new();

    private DateTimeOffset? _completedAt;

    private int _dropped;

    /// <summary>
    /// How many finished passes wait here for an ADL that is not answering.
    /// </summary>
    /// <remarks>
    /// A machine on a ten-minute cycle with twenty folder groups makes some
    /// hundred and twenty passes an hour, so this is the better part of a
    /// working day of silence before anything is shed -- and shedding costs
    /// only ADL's copy, because the cycle log on the machine keeps every pass
    /// regardless (wmo-raf/adl#306). That is the whole reason a bound this
    /// modest is safe.
    /// </remarks>
    public const int Capacity = 1_000;

    /// <summary>
    /// How many go in one beat.
    /// </summary>
    /// <remarks>
    /// A beat at five minutes against a cycle at ten carries about half a
    /// cycle's passes, so this is generous in the ordinary case and is really
    /// a bound on the catch-up beat after an outage: a machine that has been
    /// unable to reach ADL for hours empties its queue over a few beats
    /// rather than in one body ADL has to hold in memory at once.
    /// </remarks>
    public const int PerBeat = 200;

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

    /// <summary>Take one finished pass, whole.</summary>
    /// <remarks>
    /// Called by the cycle at the same moment it writes the pass to the
    /// machine's own log, and from the same record -- the two say the same
    /// thing about the same pass by construction rather than by two people
    /// remembering to keep them in step. Every pass goes on the queue,
    /// including the uneventful ones: a station producing a file every ten
    /// minutes offers one on every cycle, so filtering the quiet passes out
    /// would save rows only on the stations where "the agent looked and there
    /// was nothing" is the valuable fact -- and where its absence is
    /// indistinguishable from an agent that never ran.
    /// <para>
    /// Full, the oldest goes. A queue that dropped the newest would answer
    /// "what is wrong with this machine now" with a fortnight-old pass.
    /// </para>
    /// </remarks>
    public void Enqueue(CycleRecord record)
    {
        var pass = PassReports.Of(record);

        lock (_gate)
        {
            while (_passes.Count >= Capacity)
            {
                _passes.Dequeue();
                _dropped++;
            }

            _passes.Enqueue(pass);
        }
    }

    /// <summary>
    /// What the next beat should carry, without giving it up.
    /// </summary>
    /// <remarks>
    /// Read and not removed, because a beat ADL refuses is a beat that never
    /// arrived and its passes are still owed. They leave the queue when
    /// <see cref="Delivered"/> says ADL took them, so a machine on a link
    /// that keeps dropping loses nothing to the drops themselves -- only,
    /// eventually, to the ceiling.
    /// </remarks>
    public PassBatch Take(int most = PerBeat)
    {
        lock (_gate)
        {
            return new PassBatch(
                _passes.Take(Math.Max(0, most)).ToList(),
                _dropped);
        }
    }

    /// <summary>ADL took the first <paramref name="count"/> of them.</summary>
    /// <remarks>
    /// Takes the shed count away too, and only here: the number is a fact
    /// about a gap in ADL's history, so it is cleared when ADL has been told
    /// about it and not when it was merely put in a body that may not have
    /// arrived.
    /// </remarks>
    public void Delivered(PassBatch batch)
    {
        lock (_gate)
        {
            for (var taken = 0; taken < batch.Passes.Count && _passes.Count > 0; taken++)
            {
                _passes.Dequeue();
            }

            _dropped = Math.Max(0, _dropped - batch.Dropped);
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

/// <summary>
/// What one beat is carrying: the passes, and how many were shed to make room
/// for them.
/// </summary>
/// <remarks>
/// One value rather than two, because they are read together and settled
/// together: the beat that reports the gap is the beat that clears it, and
/// separating them produced exactly one way to clear a gap ADL was never told
/// about.
/// </remarks>
/// <param name="Dropped">
/// Passes the queue shed since ADL last accepted a beat. Zero on a machine
/// whose link is working, which is nearly all of them.
/// </param>
public sealed record PassBatch(IReadOnlyList<CyclePassReport> Passes, int Dropped);
