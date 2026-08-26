using AdlAgent.Core.Api;
using AdlAgent.Core.Configuration;
using AdlAgent.Core.State;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Cycle;

/// <summary>
/// Which stations offer ADL their whole folder this cycle, rather than only
/// what the candidate window admits.
/// </summary>
/// <remarks>
/// The correctness backstop, and the reason the cheap path is allowed to be
/// cheap. An ordinary cycle offers what is at or after ADL's watermark for a
/// station, and everything about how a file gets into a folder that the
/// watermark can miss -- a filesystem that has no creation time, a vendor's
/// archiving job putting a month back, a clock that was wrong when the file
/// was written, a watermark ADL raises once it can -- is caught here instead
/// of being reasoned about there. Once a day (ADL's number) the station
/// offers everything its pattern matches back to the collection start date
/// and lets the ledger diff sort it out. The invariant that buys is the one
/// worth having: anything in the folder that ADL lacks is eventually offered,
/// however it got there.
/// <para>
/// A sweep is <em>only</em> a lower floor. The same walk, the same pattern,
/// the same readiness check, the same hashes -- so it costs manifest pages
/// rather than a second pass over the disk, and a folder ADL already holds
/// entirely answers it with "I have all of those".
/// </para>
/// <para>
/// A DIRECT_FETCH station has no folder to re-walk and no lower floor to find
/// anything with, so what it reconciles is its own reach: an ordinary cycle
/// stops at <see cref="ExpectedFiles.MostPerCycle"/> names and a reconciling
/// one goes back to the collection start date. Decision #267 leaves exactly
/// that room ("skipped, or rare/off-hours, configurable"), and it is what
/// keeps a file recovered three weeks late from being looked for on no cycle
/// at all.
/// </para>
/// </remarks>
public sealed class ReconciliationSweep
{
    /// <summary>How often a station's whole folder is offered, absent ADL saying.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(24);

    private readonly IAgentStateStore _store;
    private readonly ILogger<ReconciliationSweep> _logger;
    private readonly Lock _gate = new();

    private Dictionary<long, DateTimeOffset>? _swept;

    public ReconciliationSweep(IAgentStateStore store, ILogger<ReconciliationSweep> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>The stations due a full offer this cycle.</summary>
    /// <remarks>
    /// A station nobody has ever swept is due immediately, which is what a
    /// fresh install wants: its first cycle is the one pass that has to see
    /// the whole folder.
    /// </remarks>
    public SweepPlan Plan(AgentConfiguration configuration, DateTimeOffset now)
    {
        var interval = Interval(configuration.Sync.Device.ReconciliationIntervalHours);

        var known = new HashSet<long>();
        var due = new HashSet<long>();

        lock (_gate)
        {
            var swept = Swept();

            foreach (var connection in configuration.Sync.Connections)
            {
                foreach (var link in connection.StationLinks)
                {
                    if (!connection.Admin.Enabled || !link.Admin.Enabled)
                    {
                        // Switched off centrally, and so out of the fleet as
                        // far as the log is concerned: dropping it here is
                        // what prunes it below.
                        continue;
                    }

                    known.Add(link.Id);

                    if (interval is null)
                    {
                        continue;
                    }

                    if (!swept.TryGetValue(link.Id, out var last) || now - last >= interval)
                    {
                        due.Add(link.Id);
                    }
                }
            }
        }

        if (due.Count > 0)
        {
            _logger.LogInformation(
                "Reconciling {Count} station(s) this cycle: offering everything back to the collection start date.",
                due.Count);
        }

        return new SweepPlan(due, known);
    }

    /// <summary>
    /// Remember which stations were reconciled.
    /// </summary>
    /// <param name="reconciled">
    /// The stations the scan actually reconciled, which is not always the
    /// ones <paramref name="plan"/> asked for: a station with no folder path,
    /// no file pattern, or Direct Fetch settings that do not add up is not
    /// scanned at all. Stamping such a station as swept would spend its day's
    /// reconciliation on a cycle that never looked at it, and leave it
    /// waiting another day after somebody fixed it.
    /// </param>
    /// <remarks>
    /// Called only when the cycle ran to its end. A sweep that was cut short
    /// by an ADL that stopped answering offered some of the folder and not
    /// the rest, and recording that as done would leave the unoffered part
    /// waiting another day for no reason. That an unreachable ADL therefore
    /// makes every cycle a sweep costs nothing: the manifest call it would
    /// spend them on is refused before it is built.
    /// </remarks>
    public void Record(SweepPlan plan, IReadOnlySet<long> reconciled, DateTimeOffset at)
    {
        lock (_gate)
        {
            var swept = Swept();
            var changed = false;

            foreach (var stationLinkId in reconciled)
            {
                swept[stationLinkId] = at;
                changed = true;
            }

            // Stations this device no longer has drop out, so the log stays
            // the size of the fleet rather than the size of its history. A
            // station that comes back is swept once, which is what a station
            // whose folder nobody watched for a while wants anyway.
            //
            // Only for a plan that saw the whole fleet. A collect-now's plan
            // knows one station, and everything else is absent from it because
            // nobody asked rather than because it has gone.
            foreach (var stationLinkId in plan.Prunes
                ? swept.Keys.Where(id => !plan.Known.Contains(id)).ToList()
                : [])
            {
                swept.Remove(stationLinkId);
                changed = true;
            }

            if (!changed)
            {
                // Nothing swept and nothing gone: not worth a disk write
                // every check interval for the life of the install.
                return;
            }

            _store.SaveSweeps(new SweepLog { Swept = swept });
        }
    }

    /// <summary>The log, read from disk once and kept.</summary>
    private Dictionary<long, DateTimeOffset> Swept() =>
        _swept ??= new Dictionary<long, DateTimeOffset>(_store.LoadSweeps().Swept);

    /// <summary>
    /// How often ADL wants a full offer, or <c>null</c> for never.
    /// </summary>
    /// <remarks>
    /// Zero or less is a deployment switching sweeps off -- a real choice for
    /// an instance whose links cannot afford them -- and is obeyed rather
    /// than clamped away. An absent field is an older ADL that does not know
    /// about the setting, and gets the daily default.
    /// <para>
    /// Public because the tray shows this cadence, and a window that read the
    /// raw number itself would be a second opinion about what a zero means.
    /// There is one reading of ADL's number and this is it.
    /// </para>
    /// </remarks>
    public static TimeSpan? Interval(int? hours) => hours switch
    {
        null => DefaultInterval,
        <= 0 => null,
        _ => TimeSpan.FromHours(Math.Min(hours.Value, 24 * 365)),
    };
}

/// <summary>
/// One cycle's sweep decision: who is being reconciled, and who could have
/// been.
/// </summary>
/// <remarks>
/// Passed from the decision to the scan and back to the recording rather than
/// held on <see cref="ReconciliationSweep"/> between calls, so that a cycle
/// that dies in the middle cannot leave the next one believing a sweep it
/// never finished.
/// </remarks>
/// <param name="Links">The stations asked to offer everything this cycle.</param>
/// <param name="Known">
/// Every station this device still has, whether or not it is being
/// reconciled. What is not in here has been moved to another machine or
/// switched off in ADL, and drops out of the log rather than being remembered
/// for the life of the install.
/// </param>
public sealed record SweepPlan(IReadOnlySet<long> Links, IReadOnlySet<long> Known)
{
    /// <summary>
    /// True when <see cref="Known"/> is the whole fleet, and so when a station
    /// missing from it has really gone.
    /// </summary>
    /// <remarks>
    /// False for the plan a collect-now builds, which is drawn from a
    /// configuration narrowed to one station link. Every other station on the
    /// machine is absent from such a plan because nobody asked about it, and
    /// pruning on that would wipe the entire sweep log every time a technician
    /// pressed a button -- so the next scheduled cycle would sweep all forty
    /// stations at once, offering every folder in full, on the link this
    /// product exists for.
    /// </remarks>
    public bool Prunes { get; init; } = true;

    /// <summary>True when this station offers its whole folder this cycle.</summary>
    public bool Includes(long stationLinkId) => Links.Contains(stationLinkId);
}
