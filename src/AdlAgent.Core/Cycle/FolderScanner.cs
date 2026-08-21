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
/// Three ideas do all the work here, and every one of them is about a folder
/// with a hundred thousand files in it.
/// <para>
/// <b>One walk per folder.</b> Station links are grouped by the folder they
/// name before anything is read, and each folder is walked once with every
/// entry offered to every pattern that folder serves. A country whose vendor
/// writes forty stations into one dump directory pays for one walk, not
/// forty.
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
    public ScanResult Scan(AgentConfiguration configuration, DateTimeOffset now)
    {
        var tallies = new Dictionary<long, LinkTally>();
        var folders = new Dictionary<string, List<Target>>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in Targets(configuration, tallies))
        {
            var folder = Normalise(target.Link.Config.LocalFolderPath);

            if (!folders.TryGetValue(folder, out var sharing))
            {
                sharing = [];
                folders[folder] = sharing;
            }

            sharing.Add(target);
        }

        var candidates = new List<FileCandidate>();

        foreach (var (folder, targets) in folders)
        {
            Walk(folder, targets, now, candidates);
        }

        // Newest first, and by name where two files share a timestamp, so
        // that a page boundary falls in the same place twice and a failure
        // message names the same file twice.
        candidates.Sort(static (left, right) =>
        {
            var byTime = right.Entry.Mtime.CompareTo(left.Entry.Mtime);

            return byTime != 0
                ? byTime
                : string.Compare(left.Entry.Name, right.Entry.Name, StringComparison.OrdinalIgnoreCase);
        });

        return new ScanResult(candidates, tallies);
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
        AgentConfiguration configuration, Dictionary<long, LinkTally> tallies)
    {
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

                if (!ListingStrategies.IsEnumerate(link.Config.ListingStrategy))
                {
                    tally.Note(
                        $"This station is set to {link.Config.ListingStrategy}, which this version of the agent does not do.");

                    continue;
                }

                if (string.IsNullOrWhiteSpace(link.Config.LocalFolderPath))
                {
                    tally.Note("No local folder is set for this station.");

                    continue;
                }

                var pattern = FilePattern.For(link.Config.FilePattern);

                if (pattern.IsEmpty)
                {
                    tally.Note("No file pattern is set for this station, so no file can be said to be its own.");

                    continue;
                }

                yield return new Target(link, pattern, tally, configuration.Sync.Limits);
            }
        }
    }

    /// <summary>One folder, walked once, offered to everything that shares it.</summary>
    private void Walk(
        string folder, List<Target> targets, DateTimeOffset now, List<FileCandidate> candidates)
    {
        var entries = 0;

        foreach (var facts in _files.Enumerate(folder))
        {
            entries++;

            foreach (var target in targets)
            {
                if (target.Pattern.Matches(facts.Name))
                {
                    Consider(target, facts, now, candidates);
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

    /// <summary>One entry, against one station's rules.</summary>
    private void Consider(
        Target target, FileFacts facts, DateTimeOffset now, List<FileCandidate> candidates)
    {
        target.Tally.Scanned++;

        var watermark = target.Link.Watermark;

        if (watermark is not null && facts.WindowTimestamp < watermark)
        {
            // Behind the floor ADL put under this station. Nothing is wrong;
            // this is how the window keeps a settled folder cheap.
            return;
        }

        if (facts.Length > target.Limits.FileBytes)
        {
            // Offering it would waste a manifest slot and an upload every
            // cycle for a file ADL is required to refuse.
            target.Tally.Failed++;
            target.Tally.Note(
                $"{facts.Name} is {facts.Length} bytes, more than the {target.Limits.FileBytes} ADL accepts.");

            return;
        }

        if (!_readiness.IsReadyToRead(facts, target.Link.Config.StabilityWindow, now))
        {
            // Still being written, or held open by whoever is writing it.
            // Not a failure -- the newest file in a live folder is in this
            // state on every single cycle -- just not this cycle's business.
            target.Tally.Pending++;

            return;
        }

        string hash;

        try
        {
            hash = _hashes.Hash(facts);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            target.Tally.Failed++;
            target.Tally.Note($"{facts.Name} could not be read: {exception.Message}");

            _logger.LogWarning(exception, "Could not read {Path} to hash it.", facts.Path);

            return;
        }

        candidates.Add(new FileCandidate(
            facts.Path,
            new ManifestEntry
            {
                StationLinkId = target.Link.Id,
                Name = facts.Name,
                Size = facts.Length,
                Mtime = facts.WindowTimestamp,
                Hash = hash,
            }));
    }

    /// <summary>
    /// The folder as a grouping key: the same directory spelled two ways is
    /// still one walk.
    /// </summary>
    /// <remarks>
    /// Written by hand rather than with the framework's path helpers, which
    /// know only the separators of the machine they are running on -- and the
    /// paths here were typed on a Windows server and may well be read on a
    /// Linux one.
    /// </remarks>
    private static string Normalise(string folder)
    {
        var trimmed = folder.Trim();
        var end = trimmed.Length;

        while (end > 1 && (trimmed[end - 1] == '/' || trimmed[end - 1] == '\\') && trimmed[end - 2] != ':')
        {
            end--;
        }

        return trimmed[..end];
    }

    /// <summary>One station link's share of one folder's walk.</summary>
    private sealed record Target(
        StationLinkConfig Link, FilePattern Pattern, LinkTally Tally, AgentLimits Limits);
}
