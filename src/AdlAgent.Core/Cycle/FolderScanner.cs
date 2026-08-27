using AdlAgent.Core.Api;
using AdlAgent.Core.Configuration;
using AdlAgent.Core.Platform;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Cycle;

/// <summary>
/// What this machine has that ADL might want: one walk per folder, whatever
/// the configuration says about it.
/// </summary>
/// <remarks>
/// Four ideas do all the work here, and every one of them is about a folder
/// with a hundred thousand files in it.
/// <para>
/// <b>One walk per folder.</b> Station links are grouped by the folder they
/// name before anything is read, and each folder is walked once with every
/// entry offered to every pattern that folder serves. A country whose vendor
/// writes forty stations into one dump directory pays for one walk, not
/// forty.
/// </para>
/// <para>
/// <b>Or no walk at all.</b> A station ADL has put on DIRECT_FETCH does not
/// walk its folder: it builds the filenames its vendor's clock implies and
/// asks the filesystem about those exact names (see
/// <see cref="ExpectedFiles"/>). That is the escape hatch for the folder
/// where the walk itself is the problem -- a million minute-by-minute files
/// for every station in the country -- and the two strategies meet again
/// immediately afterwards: same size cap, same readiness check, same lazy
/// hashing, same newest-first order.
/// </para>
/// <para>
/// <b>A station's folder is not always one folder.</b> A vendor that files by
/// date writes into <c>2026\08\21</c> below the folder an administrator
/// typed, so such a station expands to the dated directories it actually
/// holds files in (see <see cref="DatedFolders"/>) and every one of them
/// joins the same grouping as any other folder -- two stations sharing a
/// dated tree still walk it once between them.
/// </para>
/// <para>
/// <b>Cheapest question first.</b> An entry is filtered against the
/// watermark, then the size cap, then asked whether it is finished being
/// written, and only then read. The walk hands over name, size and time for
/// nothing; opening the file costs a seek; hashing it costs the whole file.
/// So the expensive question is asked of as few files as possible.
/// </para>
/// <para>
/// <b>Newest first.</b> A fresh install facing months of backlog must put
/// today's observations on the wire in its first cycle and let history fill
/// in behind (story 18), which the core's upsert-by-observation-time makes
/// harmless.
/// </para>
/// </remarks>
public sealed class FolderScanner
{
    private readonly IFileMetadataSource _files;
    private readonly IFileReadinessProbe _readiness;
    private readonly FileHashCache _hashes;
    private readonly ILogger<FolderScanner> _logger;

    public FolderScanner(
        IFileMetadataSource files,
        IFileReadinessProbe readiness,
        FileHashCache hashes,
        ILogger<FolderScanner> logger)
    {
        _files = files;
        _readiness = readiness;
        _hashes = hashes;
        _logger = logger;
    }

    /// <summary>
    /// One collection unit: stations, and the folders they share, grouped so
    /// that nothing is shared across units.
    /// </summary>
    /// <remarks>
    /// The unit a cycle is made of. It is <em>not</em> the station and not
    /// the folder, because neither survives the two ways they fail to line
    /// up: several stations write into one dump directory, which has to be
    /// walked once between them, and one station filed by date is spread
    /// over as many dated directories as its window holds.
    /// <para>
    /// So a unit is whatever a station and a folder drag in with them -- and
    /// in the overwhelming case, where a station has one folder to itself,
    /// that is one station and one folder. Grouping this way is what lets a
    /// unit be scanned, delivered and <em>reported</em> on its own: a
    /// station's sentence cannot be written until every folder of its own has
    /// been walked (see <see cref="Diagnose"/>), and no station here has a
    /// folder in anybody else's unit.
    /// </para>
    /// <para>
    /// It is also what keeps the tallies free of locks. A unit owns its
    /// stations' tallies outright, so two units running at once are never
    /// writing to the same counters.
    /// </para>
    /// </remarks>
    public sealed class ScanUnit
    {
        // The unit is public because a caller holds one between planning
        // and scanning; what is inside it is the assembly's business. A
        // half-planned walk is of no use to anybody outside this scanner.
        internal ScanUnit(
            List<Sought> walking, List<Fetched> fetching,
            Dictionary<long, LinkTally> tallies)
        {
            Walking = walking;
            Fetching = fetching;
            Tallies = tallies;
        }

        internal List<Sought> Walking { get; }

        internal List<Fetched> Fetching { get; }

        internal Dictionary<long, LinkTally> Tallies { get; }

        /// <summary>The stations this unit answers for.</summary>
        /// <remarks>
        /// Every enabled station on the machine is in exactly one unit,
        /// including the ones this cycle will not scan -- no folder, no
        /// pattern, a listing strategy this build does not know. Such a
        /// station has a tally carrying its reason and nothing to walk, and
        /// it is in a unit of its own so that the reason still reaches an
        /// operator. A station that quietly does nothing is the failure this
        /// product exists to stop shipping to countries.
        /// </remarks>
        public IReadOnlyCollection<long> StationLinkIds => Tallies.Keys;
    }

    /// <summary>
    /// What this cycle will collect, as the units it will collect it in.
    /// </summary>
    /// <remarks>
    /// Nothing is read here. This decides the shape of the cycle -- who
    /// shares a folder with whom -- and the reading happens one unit at a
    /// time in <see cref="Scan"/>, which is what keeps a folder with a
    /// hundred thousand files in it from being held in memory beside every
    /// other folder on the machine.
    /// </remarks>
    /// <param name="sweep">
    /// The stations offering their whole folder this cycle rather than only
    /// what the candidate window admits. See <see cref="ReconciliationSweep"/>.
    /// </param>
    public IReadOnlyList<ScanUnit> Plan(
        AgentConfiguration configuration, SweepPlan sweep, DateTimeOffset now)
    {
        var tallies = new Dictionary<long, LinkTally>();

        // What each station is: one or more folders to walk, or a list of
        // names to ask after. Worked out in full before anything is grouped,
        // because a folder cannot be walked once for everything that shares
        // it until everything that shares it is known.
        var order = new List<long>();
        var plan = Plan(configuration, sweep, now, tallies, order);

        return Group(plan, tallies, order);
    }

    /// <summary>
    /// Every file this unit has worth offering, newest first, and its tally
    /// per station.
    /// </summary>
    /// <remarks>
    /// Newest first <em>within the unit</em>, which is where the ordering was
    /// always doing its work. Sorting the whole machine at once bought
    /// nothing a unit-at-a-time sort does not -- every station still puts
    /// today's observations in front of its own history (story 18) -- and
    /// cost holding every file on the machine in one list to do it.
    /// </remarks>
    public ScanResult Scan(ScanUnit unit, DateTimeOffset now)
    {
        var folders = new Dictionary<string, List<Walked>>(StringComparer.Ordinal);

        // What was reconciled, as opposed to what the plan asked for. A
        // station the scan turns away -- no folder, no pattern, Direct Fetch
        // settings that do not add up -- did not offer anything, let alone
        // everything, and must not have its day's reconciliation spent on it.
        var reconciled = new HashSet<long>();

        foreach (var target in unit.Walking.SelectMany(sought => sought.Targets).Concat<Target>(unit.Fetching))
        {
            if (target.Reconciling)
            {
                reconciled.Add(target.Link.Id);
            }

            if (target is not Walked walked)
            {
                continue;
            }

            var key = GroupingKey(walked.Folder);

            if (!folders.TryGetValue(key, out var sharing))
            {
                sharing = [];
                folders[key] = sharing;
            }

            sharing.Add(walked);
        }

        var matched = new List<Match>();

        foreach (var targets in folders.Values)
        {
            // The folder as ADL spells it, not the key it was grouped under.
            // What goes to the seam is always a path an administrator typed,
            // or one dated directory below it; the key is this method's
            // private business.
            Walk(targets[0].Folder, targets, now, matched);
        }

        Diagnose(unit.Walking);

        foreach (var target in unit.Fetching)
        {
            Fetch(target, now, matched);
        }

        // Newest first, and by name where two files share a timestamp, so
        // that a page boundary falls in the same place twice and a failure
        // message names the same file twice.
        matched.Sort(static (left, right) =>
        {
            var byTime = right.Facts.WindowTimestamp.CompareTo(left.Facts.WindowTimestamp);

            return byTime != 0
                ? byTime
                : string.Compare(left.Facts.Name, right.Facts.Name, StringComparison.OrdinalIgnoreCase);
        });

        return new ScanResult(Hashing(matched), unit.Tallies, reconciled);
    }

    /// <summary>
    /// The plan, cut into units: stations joined to every station they share
    /// a folder with.
    /// </summary>
    /// <remarks>
    /// A breadth-first walk of the small bipartite graph of stations and
    /// folder keys. Two stations are in one unit when a folder joins them,
    /// and transitively -- a dated tree shared by two stations, each of which
    /// also has a folder of its own, is one unit of two stations and all
    /// their folders.
    /// <para>
    /// A station the scan turned away has no folder to join it to anything,
    /// so it lands in a unit by itself, carrying the tally that says why.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ScanUnit> Group(
        ScanPlan plan, Dictionary<long, LinkTally> tallies, IReadOnlyList<long> order)
    {
        // Every station's folders, and every folder's stations. Built once,
        // then walked; the graph is the size of the fleet, not of the disks.
        var foldersOf = new Dictionary<long, List<string>>();
        var stationsOn = new Dictionary<string, List<long>>(StringComparer.Ordinal);

        foreach (var sought in plan.Walking)
        {
            foreach (var walked in sought.Targets)
            {
                var key = GroupingKey(walked.Folder);

                if (!foldersOf.TryGetValue(walked.Link.Id, out var keys))
                {
                    keys = [];
                    foldersOf[walked.Link.Id] = keys;
                }

                keys.Add(key);

                if (!stationsOn.TryGetValue(key, out var links))
                {
                    links = [];
                    stationsOn[key] = links;
                }

                links.Add(walked.Link.Id);
            }
        }

        // Indexed rather than searched per station: a device may serve forty
        // stations across as many folders, and this is walked once each.
        var walkingOf = plan.Walking.ToDictionary(sought => sought.Tally.StationLinkId);
        var fetchingOf = plan.Fetching.ToDictionary(fetched => fetched.Link.Id);

        var units = new List<ScanUnit>();
        var placed = new HashSet<long>();

        // In the order the plan stated them -- which is ADL's order, through
        // the configuration -- so that a cycle visits stations in the same
        // order twice and a log reads the same way on Tuesday as on Monday.
        // From a list rather than from the tally dictionary's keys: a
        // Dictionary's enumeration order is not a promise it makes, and this
        // is a promise being made.
        foreach (var stationLinkId in order)
        {
            if (!placed.Add(stationLinkId))
            {
                continue;
            }

            var members = new List<long> { stationLinkId };
            var queue = new Queue<long>();
            queue.Enqueue(stationLinkId);

            while (queue.Count > 0)
            {
                foreach (var key in Folders(foldersOf, queue.Dequeue()))
                {
                    foreach (var neighbour in stationsOn[key])
                    {
                        if (placed.Add(neighbour))
                        {
                            members.Add(neighbour);
                            queue.Enqueue(neighbour);
                        }
                    }
                }
            }

            units.Add(new ScanUnit(
                members.Where(walkingOf.ContainsKey).Select(id => walkingOf[id]).ToList(),
                members.Where(fetchingOf.ContainsKey).Select(id => fetchingOf[id]).ToList(),
                members.ToDictionary(id => id, id => tallies[id])));
        }

        return units;
    }

    private static IReadOnlyList<string> Folders(
        Dictionary<long, List<string>> foldersOf, long stationLinkId) =>
        foldersOf.TryGetValue(stationLinkId, out var keys) ? keys : [];

    /// <summary>
    /// The matched files, hashed as they are asked for and not before.
    /// </summary>
    /// <remarks>
    /// Lazily, and that is the whole of story 18. Sorting puts today's files
    /// at the front; hashing them all first would mean a fresh install facing
    /// a year of backlog reads every one of those files before ADL hears
    /// about a single one of them. Handing the cycle a sequence instead lets
    /// it read five hundred files, send them, and only then start on the
    /// history behind -- so current observations move in the first minute of
    /// the first cycle rather than after the folder has been chewed through.
    /// <para>
    /// A file that cannot be read is charged to its station and skipped: one
    /// unreadable file is not a reason to stop offering the rest.
    /// </para>
    /// <para>
    /// Worth knowing when changing this: no test can catch it being made
    /// eager again. The same calls go out in the same order either way, and
    /// what differs -- how long the first upload waits on a folder nobody has
    /// looked at in a year -- is invisible at every seam the tests can reach.
    /// </para>
    /// </remarks>
    private IEnumerable<FileCandidate> Hashing(List<Match> matched)
    {
        foreach (var match in matched)
        {
            string hash;

            try
            {
                hash = _hashes.Hash(match.Facts);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                match.Target.Tally.Fail($"{match.Facts.Name} could not be read: {exception.Message}");

                _logger.LogWarning(exception, "Could not read {Path} to hash it.", match.Facts.Path);

                continue;
            }

            yield return new FileCandidate(
                match.Facts.Path,
                new ManifestEntry
                {
                    StationLinkId = match.Target.Link.Id,
                    Name = match.Facts.Name,
                    Size = match.Facts.Length,
                    Mtime = match.Facts.WindowTimestamp,
                    Hash = hash,
                });
        }
    }

    /// <summary>
    /// What this cycle will look at, with a tally opened for every link the
    /// device has -- including the ones that will not be scanned.
    /// </summary>
    /// <remarks>
    /// A link that cannot be scanned still gets a tally, carrying the reason.
    /// A station that quietly does nothing, cycle after cycle, is the failure
    /// this whole product exists to stop shipping to countries: it has to
    /// arrive at HQ as a sentence, not as an absence.
    /// <para>
    /// Worked out in full rather than yielded lazily. Everything after this
    /// depends on the whole fleet having been planned -- the grouping that
    /// makes two stations share one walk, and the per-station sentence that
    /// cannot be written until every folder of that station has been walked
    /// -- so a caller that stopped part-way would silently lose both.
    /// </para>
    /// </remarks>
    private ScanPlan Plan(
        AgentConfiguration configuration,
        SweepPlan sweep,
        DateTimeOffset now,
        Dictionary<long, LinkTally> tallies,
        List<long> order)
    {
        var walking = new List<Sought>();
        var fetching = new List<Fetched>();

        // One compiled glob per distinct pattern, for the length of this
        // scan. Several stations sharing a naming convention is the norm,
        // and this is also what keeps the compiled regex from outliving the
        // configuration that asked for it.
        var patterns = new Dictionary<string, FilePattern>(StringComparer.Ordinal);

        foreach (var connection in configuration.Sync.Connections)
        {
            foreach (var link in connection.StationLinks)
            {
                if (!connection.Admin.Enabled || !link.Admin.Enabled)
                {
                    // Switched off centrally. Not scanned and not reported
                    // against: this is an administrator's decision, not a
                    // fault of the machine's.
                    continue;
                }

                var tally = new LinkTally(link.Id);

                tallies[link.Id] = tally;
                order.Add(link.Id);

                if (string.IsNullOrWhiteSpace(link.Config.LocalFolderPath))
                {
                    tally.Note("No local folder is set for this station.");

                    continue;
                }

                if (ListingStrategies.IsDirectFetch(link.Config.ListingStrategy))
                {
                    if (link.Config.DirStructuredByDate)
                    {
                        // A station that both files by date and builds its
                        // filenames would need the date in the folder and in
                        // the name, which no vendor this has been built
                        // against does. Said out loud, because the alternative
                        // is a station asking after names in a folder its
                        // vendor stopped writing to and reporting only that
                        // it found none of them -- which reads as a mistyped
                        // prefix and sends somebody changing the wrong thing.
                        tally.Note(
                            "This station builds its filenames and also says they are in dated sub-folders. "
                            + "The agent looks for them in the folder itself. Clear one of the two settings in "
                            + "ADL.");
                    }

                    if (Fetching(link, tally, configuration.Sync.Limits, sweep, now) is { } fetched)
                    {
                        fetching.Add(fetched);
                    }

                    continue;
                }

                if (!ListingStrategies.IsEnumerate(link.Config.ListingStrategy))
                {
                    // Said rather than guessed at. A newer ADL naming a
                    // strategy this version does not implement is the one
                    // case where doing the familiar thing anyway would be
                    // worst: the folder it names may be enormous, and walking
                    // it is exactly what the unknown strategy was chosen to
                    // avoid.
                    tally.Note(
                        $"This station is set to {link.Config.ListingStrategy}, which this version of the agent "
                        + "does not know. Update the agent, or set it back to enumerate in ADL.");

                    continue;
                }

                var sought = Walking(
                    link, tally, configuration.Sync.Limits, patterns, sweep, now,
                    DatedFolders.RecentWindow(configuration.Sync.Device.DatedFolderWindowHours));

                if (sought is null)
                {
                    continue;
                }

                walking.Add(sought);
            }
        }

        return new ScanPlan(walking, fetching);
    }

    /// <summary>
    /// One station whose files are found by walking, and every folder that
    /// means.
    /// </summary>
    /// <remarks>
    /// One folder for almost every station. A station filed by date is the
    /// exception, and it is one folder per dated directory in the window --
    /// each of them an ordinary walking target from that point on, grouped
    /// and walked exactly like any other folder, so a second station sharing
    /// the tree costs nothing.
    /// </remarks>
    private Sought? Walking(
        StationLinkConfig link,
        LinkTally tally,
        AgentLimits limits,
        Dictionary<string, FilePattern> patterns,
        SweepPlan sweep,
        DateTimeOffset now,
        TimeSpan recentWindow)
    {
        var glob = link.Config.FilePattern ?? "";

        if (!patterns.TryGetValue(glob, out var pattern))
        {
            pattern = FilePattern.For(glob);
            patterns[glob] = pattern;
        }

        if (pattern.IsEmpty)
        {
            tally.Note("No file pattern is set for this station, so no file can be said to be its own.");

            return null;
        }

        // A sweep is a lower floor and nothing else. ADL's watermark is what
        // makes an ordinary cycle cheap; the collection start date is as far
        // back as this station's files were ever wanted, and once a day that
        // is what the folder is offered against instead. Everything the
        // watermark can miss -- a filesystem with no creation time, a
        // vendor's archiving job putting a month back, a clock that was wrong
        // when the file was written -- is caught by that and not by reasoning
        // about it here.
        var reconciling = sweep.Includes(link.Id);
        var floor = reconciling ? Lower(link.Watermark, link.Admin.StartDate) : link.Watermark;
        var root = link.Config.LocalFolderPath.Trim();

        if (!link.Config.DirStructuredByDate)
        {
            return new Sought(tally, pattern, root, false,
                [new Walked(link, tally, limits, reconciling, root, pattern, floor)]);
        }

        // How far back the directories go, which is not the same question as
        // how old a file may be. An ordinary cycle takes the recent window and
        // a sweep takes ADL's floor, and neither is the other: a folder named
        // for yesterday can hold a file written this morning -- a logger
        // filling yesterday's file past midnight, a backfill copied in -- so
        // raising the directory floor to the watermark would skip exactly the
        // directory that file is in. What the watermark filters is files, and
        // it still does, inside every folder walked.
        //
        // A station ADL has put no floor under at all cannot be swept deeper
        // than it is walked: nothing says how far back its files were ever
        // wanted, and picking a number would be this agent guessing at an
        // administrator's decision.
        var from = reconciling
            ? Deepest(link.Watermark, link.Admin.StartDate) ?? now - recentWindow
            : now - recentWindow;
        var expanded = DatedFolders.For(
            link.Config, link.Admin.Timezone, from, now,
            reconciling ? DatedFolders.MostPerSweep : DatedFolders.MostPerCycle);

        if (expanded.Problem is not null)
        {
            tally.Note(expanded.Problem);

            return null;
        }

        if (expanded.Truncated)
        {
            // The bound stopped the expansion before ADL's floor did, so this
            // station is looking at less than it was configured for -- and
            // said out loud either way, because a bound that silently
            // overrides a setting is the setting not working.
            tally.Note(reconciling
                ? $"This station's dated folders go back further than the {DatedFolders.MostPerSweep} even a "
                    + "full reconciliation walks, so its oldest are out of reach. Move its collection start "
                    + "date forward in ADL, or file it at a coarser granularity."
                : $"This station's window asks for more dated folders than the {DatedFolders.MostPerCycle} a "
                    + "cycle walks, so it is walking the newest of them. Shorten the device's dated folder "
                    + "window in ADL, or file this station at a coarser granularity.");
        }

        return new Sought(tally, pattern, root, true, expanded.Segments
            .Select(segments => new Walked(
                link, tally, limits, reconciling, _files.Descend(root, segments), pattern, floor))
            .ToList());
    }

    /// <summary>
    /// The lower of two floors, where absent means no floor at all.
    /// </summary>
    /// <remarks>
    /// A sweep must never end up offering <em>less</em> than an ordinary
    /// cycle, which is why this is not simply the collection start date. ADL
    /// pulls the watermark below that date on purpose when an operator asks
    /// for a pruned file to be sent again, and a sweep that took the start
    /// date alone would raise the floor back over the very file that was
    /// asked for.
    /// </remarks>
    private static DateTimeOffset? Lower(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null || right is null)
        {
            return null;
        }

        return left < right ? left : right;
    }

    /// <summary>
    /// The older of two dates, where absent means "the other one" rather than
    /// "no floor".
    /// </summary>
    /// <remarks>
    /// Not <see cref="Lower"/>, and the difference matters only to the
    /// directories a sweep expands to. As a floor on a file's timestamp,
    /// absent means offer everything, and <see cref="Lower"/> is right to
    /// answer null the moment either side is missing. As a depth to walk a
    /// tree to, absent means ADL did not say -- and a station sent a
    /// collection start date and no watermark, which is every station ADL has
    /// not yet received anything for, would otherwise have its sweep stop at
    /// the recent window and never reach its own backlog.
    /// </remarks>
    private static DateTimeOffset? Deepest(DateTimeOffset? left, DateTimeOffset? right) =>
        (left, right) switch
        {
            (null, null) => null,
            (null, _) => right,
            (_, null) => left,
            _ => left < right ? left : right,
        };

    /// <summary>One station whose filenames are built rather than found.</summary>
    private static Fetched? Fetching(
        StationLinkConfig link,
        LinkTally tally,
        AgentLimits limits,
        SweepPlan sweep,
        DateTimeOffset now)
    {
        // The floor the names are built back to. ADL's watermark is the same
        // collection start date under a different name today, and taking the
        // watermark first is what will keep this station cheap the day ADL
        // starts raising it.
        var floor = link.Watermark ?? link.Admin.StartDate;

        // What reconciling means for a station with no folder to re-walk: it
        // stops being cut short of its own backlog. An ordinary cycle looks
        // at the newest twenty thousand names; once a day it looks at all of
        // them, which is what finds a file copied in three weeks late.
        var reconciling = sweep.Includes(link.Id);
        var mostNames = reconciling ? ExpectedFiles.MostPerSweep : ExpectedFiles.MostPerCycle;

        var expected = ExpectedFiles.For(link.Config, floor, now, mostNames);

        if (expected.Problem is not null)
        {
            tally.Note(expected.Problem);

            return null;
        }

        if (expected.Truncated && reconciling)
        {
            // Even the deep pass stopped short, which no station that is busy
            // rather than misconfigured can manage. This one is the only case
            // where backlog really is out of reach, so it is the only one
            // worth telling an operator to act on.
            tally.Note(
                $"This station expects more files than the {ExpectedFiles.MostPerSweep} even a full "
                + "reconciliation looks for, so its oldest are out of reach. Move its collection start date "
                + "forward in ADL, or widen its file interval.");
        }

        return new Fetched(link, tally, limits, reconciling, expected.Names);
    }

    /// <summary>One folder, walked once, offered to everything that shares it.</summary>
    /// <remarks>
    /// Nothing is said about this folder here, however empty it was. A
    /// station filed by date is spread over as many folders as the window
    /// holds and the newest of them does not exist until the vendor writes
    /// the day's first file -- so what a station has to say is said once it
    /// has been offered all of them, in <see cref="Diagnose"/>.
    /// </remarks>
    private void Walk(
        string folder, List<Walked> targets, DateTimeOffset now, List<Match> matched)
    {
        var entries = 0;

        foreach (var facts in _files.Enumerate(folder))
        {
            entries++;

            foreach (var target in targets)
            {
                if (target.Pattern.Matches(facts.Name))
                {
                    Consider(target, facts, target.Floor, now, matched);
                }
            }
        }

        foreach (var target in targets)
        {
            target.Entries = entries;
        }
    }

    /// <summary>
    /// What each walking station has to say for itself, once every folder of
    /// its own has been walked.
    /// </summary>
    /// <remarks>
    /// Said out loud, because the two ways a station goes silent look
    /// identical from HQ and are fixed differently: the folder is not there,
    /// or the pattern is not the vendor's naming.
    /// <para>
    /// Per station rather than per folder, which is the difference a dated
    /// tree makes. A directory the vendor has not created yet is a
    /// non-event -- every station filed by day passes through one every
    /// midnight -- and a station spread over forty-nine hourly folders would
    /// otherwise report the first empty one as though something were wrong
    /// with it. What is worth a sentence is a station that found nothing
    /// anywhere.
    /// </para>
    /// </remarks>
    private static void Diagnose(IReadOnlyList<Sought> walking)
    {
        foreach (var sought in walking.Where(sought => sought.Tally.Scanned == 0))
        {
            var entries = sought.Targets.Sum(target => target.Entries);
            var where = sought.Dated
                ? sought.Targets.Count == 1
                    ? $"the dated folder below {sought.Root} this cycle looked in"
                    : $"the {sought.Targets.Count} dated folders below {sought.Root} this cycle looked in"
                : sought.Root;

            sought.Tally.Note(entries == 0
                ? $"Nothing is in {where}, or this machine cannot see it."
                : $"None of the {entries} files in {where} match '{sought.Pattern.Text}'.");
        }
    }

    /// <summary>One station's expected filenames, asked about one at a time.</summary>
    /// <remarks>
    /// No message when nothing is found, and that is the difference between
    /// this and <see cref="Walk"/>. A walk that finds nothing has learned
    /// something -- the folder is wrong, or the pattern is -- but a
    /// constructed name that is not there has learned nothing at all: the
    /// newest expected file is always missing, because the interval it
    /// belongs to has not finished. Only a station that finds nothing across
    /// its whole window is worth a sentence.
    /// </remarks>
    private void Fetch(Fetched target, DateTimeOffset now, List<Match> matched)
    {
        var folder = target.Folder;

        foreach (var name in target.Names)
        {
            if (_files.Describe(folder, name) is { } facts)
            {
                // No floor. What put this file in the cycle was the clock in
                // its name, and filtering it again on the timestamps the
                // filesystem happens to carry would drop the backfill the
                // names were built to find.
                Consider(target, facts, floor: null, now, matched);
            }
        }

        if (target.Tally.Scanned == 0)
        {
            target.Tally.Note(
                $"None of the {target.Names.Count} filenames this station expects are in {folder}. "
                + "Check the folder, the file prefix, the datetime format and the filename timezone.");
        }
    }

    /// <summary>One entry, against one station's rules.</summary>
    private void Consider(
        Target target, FileFacts facts, DateTimeOffset? floor, DateTimeOffset now, List<Match> matched)
    {
        target.Tally.Saw();

        if (floor is not null && facts.WindowTimestamp < floor)
        {
            // Behind the floor ADL put under this station. Nothing is wrong;
            // this is how the window keeps a settled folder cheap.
            return;
        }

        if (facts.Length > target.Limits.FileBytes)
        {
            // ADL enforces this cap itself and would refuse the file. Caught
            // here as well because a refusal the agent could have predicted
            // is one it would earn again on every cycle for the life of the
            // install -- a manifest slot and an upload, forever, for a file
            // that can never be accepted.
            target.Tally.Fail(
                $"{facts.Name} is {facts.Length} bytes, more than the {target.Limits.FileBytes} ADL accepts.");

            return;
        }

        if (!_readiness.IsReadyToRead(facts, target.Link.Config.StabilityWindow, now))
        {
            // Still being written, or held open by whoever is writing it.
            // Not a failure -- the newest file in a live folder is in this
            // state on every single cycle -- just not this cycle's business.
            target.Tally.Wait();

            return;
        }

        matched.Add(new Match(facts, target));
    }

    /// <summary>
    /// What makes two station links share a walk.
    /// </summary>
    /// <remarks>
    /// Deliberately not a path: this string is only ever a dictionary key,
    /// and what reaches the file-metadata seam is the folder as ADL spells
    /// it. That is the whole reason there is no path grammar in here.
    /// Interpreting a path -- what a trailing separator means, whether
    /// <c>C:</c> and <c>C:\</c> are the same place, whether two spellings
    /// differing only in case are one folder -- is the platform's business,
    /// and the core has no business guessing at it.
    /// <para>
    /// So the key is the exact string, with surrounding whitespace and one
    /// trailing separator taken off, compared case-sensitively. Being wrong
    /// in the safe direction costs a folder two walks instead of one; being
    /// wrong the other way would have a station on a Linux server scan
    /// <c>/data</c> when it was configured for <c>/Data</c>.
    /// </para>
    /// </remarks>
    private static string GroupingKey(string folder)
    {
        var trimmed = folder.Trim();

        return trimmed.Length > 1 && (trimmed[^1] == '/' || trimmed[^1] == '\\')
            ? trimmed[..^1]
            : trimmed;
    }

    /// <summary>A file that belongs to a station and is ready to go.</summary>
    internal sealed record Match(FileFacts Facts, Target Target);

    /// <summary>
    /// One station link's share of one cycle.
    /// </summary>
    /// <param name="Reconciling">
    /// True when this station is offering everything it can rather than only
    /// what the candidate window admits. What that means differs by strategy;
    /// what it means to the cycle is the same, which is why it is here.
    /// </param>
    /// <param name="Folder">
    /// The folder this target's files are in: the one ADL spells, or -- for a
    /// station filed by date -- one dated directory below it. Carried rather
    /// than derived, because a station is only sometimes one folder.
    /// </param>
    internal abstract record Target(
        StationLinkConfig Link, LinkTally Tally, AgentLimits Limits, bool Reconciling, string Folder);

    /// <summary>A station whose files are found by walking its folder.</summary>
    /// <param name="Floor">
    /// The oldest a file's own timestamp may be, or <c>null</c> for no floor
    /// at all.
    /// </param>
    internal sealed record Walked(
        StationLinkConfig Link,
        LinkTally Tally,
        AgentLimits Limits,
        bool Reconciling,
        string Folder,
        FilePattern Pattern,
        DateTimeOffset? Floor)
        : Target(Link, Tally, Limits, Reconciling, Folder)
    {
        /// <summary>How many entries the walk of this folder went past.</summary>
        /// <remarks>
        /// Written by the walk and read by <see cref="Diagnose"/>, which is
        /// the one number that tells "this folder is not there" apart from
        /// "your pattern does not match what is in it".
        /// </remarks>
        public int Entries { get; set; }
    }

    /// <summary>A station whose filenames are built rather than found.</summary>
    /// <param name="Names">The filenames to ask the filesystem about, newest first.</param>
    internal sealed record Fetched(
        StationLinkConfig Link,
        LinkTally Tally,
        AgentLimits Limits,
        bool Reconciling,
        IReadOnlyList<string> Names)
        : Target(Link, Tally, Limits, Reconciling, Link.Config.LocalFolderPath.Trim());

    /// <summary>
    /// One walking station and every folder it turned out to be.
    /// </summary>
    /// <remarks>
    /// The scan's own bookkeeping, and the reason a station filed by date can
    /// be diagnosed as one station rather than as forty-nine folders. Not a
    /// <see cref="Target"/>: nothing walks a Sought, the walk is of the
    /// folders inside it.
    /// </remarks>
    /// <param name="Root">The folder ADL named, which is what a sentence names.</param>
    /// <param name="Dated">True when <paramref name="Root"/> is a tree rather than a folder.</param>
    internal sealed record Sought(
        LinkTally Tally,
        FilePattern Pattern,
        string Root,
        bool Dated,
        IReadOnlyList<Walked> Targets);

    /// <summary>Everything this cycle intends to look at, before it looks.</summary>
    internal sealed record ScanPlan(IReadOnlyList<Sought> Walking, IReadOnlyList<Fetched> Fetching);
}
