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
    /// Every file worth offering, newest first, and a tally per station.
    /// </summary>
    /// <param name="sweep">
    /// The stations offering their whole folder this cycle rather than only
    /// what the candidate window admits. See <see cref="ReconciliationSweep"/>.
    /// </param>
    public ScanResult Scan(AgentConfiguration configuration, SweepPlan sweep, DateTimeOffset now)
    {
        var tallies = new Dictionary<long, LinkTally>();
        var folders = new Dictionary<string, List<Walked>>(StringComparer.Ordinal);
        var fetching = new List<Fetched>();

        // What was reconciled, as opposed to what the plan asked for. A
        // station the scan turns away -- no folder, no pattern, Direct Fetch
        // settings that do not add up -- did not offer anything, let alone
        // everything, and must not have its day's reconciliation spent on it.
        var reconciled = new HashSet<long>();

        foreach (var target in Targets(configuration, sweep, now, tallies))
        {
            if (target.Reconciling)
            {
                reconciled.Add(target.Link.Id);
            }

            switch (target)
            {
                case Fetched fetched:
                    fetching.Add(fetched);

                    break;

                case Walked walked:
                    var key = GroupingKey(walked.Folder);

                    if (!folders.TryGetValue(key, out var sharing))
                    {
                        sharing = [];
                        folders[key] = sharing;
                    }

                    sharing.Add(walked);

                    break;
            }
        }

        var matched = new List<Match>();

        foreach (var targets in folders.Values)
        {
            // The folder as ADL spells it, not the key it was grouped under.
            // What goes to the seam is always a path an administrator typed;
            // the key is this method's private business.
            Walk(targets[0].Folder, targets, now, matched);
        }

        foreach (var target in fetching)
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

        return new ScanResult(Hashing(matched), tallies, reconciled);
    }

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
    /// The station links worth scanning, with a tally opened for every link
    /// the device has -- including the ones that will not be scanned.
    /// </summary>
    /// <remarks>
    /// A link that cannot be scanned still gets a tally, carrying the reason.
    /// A station that quietly does nothing, cycle after cycle, is the failure
    /// this whole product exists to stop shipping to countries: it has to
    /// arrive at HQ as a sentence, not as an absence.
    /// </remarks>
    private static IEnumerable<Target> Targets(
        AgentConfiguration configuration,
        SweepPlan sweep,
        DateTimeOffset now,
        Dictionary<long, LinkTally> tallies)
    {
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

                if (string.IsNullOrWhiteSpace(link.Config.LocalFolderPath))
                {
                    tally.Note("No local folder is set for this station.");

                    continue;
                }

                if (link.Config.DirStructuredByDate)
                {
                    // ADL lets a station say its files live under dated
                    // sub-folders, and this version walks only the folder
                    // itself. Said out loud rather than walked anyway and
                    // found empty: a station that scans zero files for a
                    // reason nobody is told is the silence this whole product
                    // exists to remove.
                    tally.Note(
                        "This station's files are in dated sub-folders, which this version of the agent does not walk. "
                        + "Point it at the folder the files are actually in, or wait for support to land.");

                    continue;
                }

                Target? target = ListingStrategies.IsDirectFetch(link.Config.ListingStrategy)
                    ? Fetching(link, tally, configuration.Sync.Limits, sweep, now)
                    : ListingStrategies.IsEnumerate(link.Config.ListingStrategy)
                        ? Walking(link, tally, configuration.Sync.Limits, patterns, sweep)
                        : Unknown(link, tally);

                if (target is not null)
                {
                    yield return target;
                }
            }
        }
    }

    /// <summary>One station whose files are found by walking its folder.</summary>
    private static Walked? Walking(
        StationLinkConfig link,
        LinkTally tally,
        AgentLimits limits,
        Dictionary<string, FilePattern> patterns,
        SweepPlan sweep)
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

        return new Walked(link, tally, limits, reconciling, pattern, floor);
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

    /// <summary>
    /// A station set to something this agent has never heard of.
    /// </summary>
    /// <remarks>
    /// Said rather than guessed at. A newer ADL naming a strategy this
    /// version does not implement is the one case where doing the familiar
    /// thing anyway would be worst: the folder it names may be enormous, and
    /// walking it is exactly what the unknown strategy was chosen to avoid.
    /// </remarks>
    private static Walked? Unknown(StationLinkConfig link, LinkTally tally)
    {
        tally.Note(
            $"This station is set to {link.Config.ListingStrategy}, which this version of the agent does not know. "
            + "Update the agent, or set it back to enumerate in ADL.");

        return null;
    }

    /// <summary>One folder, walked once, offered to everything that shares it.</summary>
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

        foreach (var target in targets.Where(target => target.Tally.Scanned == 0))
        {
            // Said out loud, because the two ways a station goes silent look
            // identical from HQ and are fixed differently: the folder is not
            // there, or the pattern is not the vendor's naming.
            target.Tally.Note(entries == 0
                ? $"Nothing is in {folder}, or this machine cannot see it."
                : $"None of the {entries} files in {folder} match '{target.Pattern.Text}'.");
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
    private sealed record Match(FileFacts Facts, Target Target);

    /// <summary>
    /// One station link's share of one cycle.
    /// </summary>
    /// <param name="Reconciling">
    /// True when this station is offering everything it can rather than only
    /// what the candidate window admits. What that means differs by strategy;
    /// what it means to the cycle is the same, which is why it is here.
    /// </param>
    private abstract record Target(
        StationLinkConfig Link, LinkTally Tally, AgentLimits Limits, bool Reconciling)
    {
        /// <summary>The folder this station's files are in, as ADL spells it.</summary>
        public string Folder => Link.Config.LocalFolderPath.Trim();
    }

    /// <summary>A station whose files are found by walking its folder.</summary>
    /// <param name="Floor">
    /// The oldest a file's own timestamp may be, or <c>null</c> for no floor
    /// at all.
    /// </param>
    private sealed record Walked(
        StationLinkConfig Link,
        LinkTally Tally,
        AgentLimits Limits,
        bool Reconciling,
        FilePattern Pattern,
        DateTimeOffset? Floor)
        : Target(Link, Tally, Limits, Reconciling);

    /// <summary>A station whose filenames are built rather than found.</summary>
    /// <param name="Names">The filenames to ask the filesystem about, newest first.</param>
    private sealed record Fetched(
        StationLinkConfig Link,
        LinkTally Tally,
        AgentLimits Limits,
        bool Reconciling,
        IReadOnlyList<string> Names)
        : Target(Link, Tally, Limits, Reconciling);
}
