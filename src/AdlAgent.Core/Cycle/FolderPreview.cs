using AdlAgent.Core.Api;
using AdlAgent.Core.Platform;

namespace AdlAgent.Core.Cycle;

/// <summary>
/// What a folder and a pattern would match, answered while the technician is
/// still typing them.
/// </summary>
/// <remarks>
/// Story 7. The person binding a station is standing in front of the machine
/// that holds the files, and is the only person who can tell whether
/// <c>GARISSA_*.dat</c> is the vendor's convention or whether it is
/// <c>garissa*.DAT</c> in a folder one level down. Making them save the
/// setting and wait out a check interval to find out turns a five-second
/// question into a next-day one, usually with a phone call to another
/// country in between.
/// <para>
/// This deliberately shares <see cref="FilePattern"/> and
/// <see cref="ExpectedFiles"/> with the scan rather than approximating them.
/// A preview that matched by its own rules would be worse than no preview:
/// it would tell the technician their pattern was right and leave the
/// station collecting nothing.
/// </para>
/// <para>
/// What it does <em>not</em> share is the scan's filters. No watermark, no
/// stability window, no readiness probe: the question here is "do these
/// settings pick out this station's files", not "which of them would go up
/// this cycle". A file too new to send is still a file that matched, and
/// showing zero because the vendor wrote it forty seconds ago would send
/// somebody looking for a fault that is not there.
/// </para>
/// </remarks>
public sealed class FolderPreview
{
    /// <summary>
    /// The most entries one preview will look at.
    /// </summary>
    /// <remarks>
    /// A bound, because the folders this product exists for hold hundreds of
    /// thousands of files and this runs while somebody is typing. What the
    /// bound costs is exactness on exactly those folders -- "more than twenty
    /// thousand match" instead of a number -- which is not a distinction
    /// anybody binding a station needs. What it buys is that the tray answers
    /// at all.
    /// </remarks>
    public const int MostEntriesExamined = 20_000;

    /// <summary>
    /// The most constructed names one preview of a DIRECT_FETCH station will
    /// ask the filesystem about.
    /// </summary>
    /// <remarks>
    /// Far below <see cref="ExpectedFiles.MostPerCycle"/>, and for a
    /// different reason than the cycle's bound: a cycle runs every ten
    /// minutes and may spend a second on twenty thousand stat calls, while
    /// this runs between two keystrokes. Five hundred is a day and a half of
    /// ten-minute files, which is enough to tell a working prefix from a
    /// mistyped one -- the only question a preview is asked.
    /// </remarks>
    public const int MostNamesTried = 500;

    /// <summary>How many matched names the answer carries back.</summary>
    private const int SampleSize = 10;

    private readonly IFileMetadataSource _files;

    public FolderPreview(IFileMetadataSource files)
    {
        _files = files;
    }

    /// <summary>
    /// Count what these settings would pick out of this machine's filesystem.
    /// </summary>
    /// <param name="now">
    /// The clock the DIRECT_FETCH names are built from. Ignored under
    /// ENUMERATE, which has no clock in it.
    /// </param>
    public FolderPreviewResult Preview(StationLinkAppConfig config, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(config.LocalFolderPath))
        {
            return Problem(config, "Type the folder this station's files are written into.");
        }

        if (config.DirStructuredByDate)
        {
            // The same sentence the cycle would report, said before the
            // setting is saved rather than a check interval later.
            return Problem(
                config,
                "This station's files are in dated sub-folders, which this version of the agent does not walk. "
                + "Point it at the folder the files are actually in.");
        }

        if (ListingStrategies.IsDirectFetch(config.ListingStrategy))
        {
            return Fetching(config, now);
        }

        if (!ListingStrategies.IsEnumerate(config.ListingStrategy))
        {
            return Problem(
                config,
                $"This station is set to {config.ListingStrategy}, which this version of the agent does not know.");
        }

        return Walking(config);
    }

    /// <summary>The enumerate strategy's answer: one walk, counted.</summary>
    private FolderPreviewResult Walking(StationLinkAppConfig config)
    {
        var pattern = FilePattern.For(config.FilePattern);

        if (pattern.IsEmpty)
        {
            return Problem(
                config,
                "Type the file pattern this station's filenames match, such as GARISSA_*.dat. "
                + "Without one no file in this folder can be said to be this station's.");
        }

        var examined = 0;
        var matches = 0;
        var truncated = false;
        var sample = new List<FileFacts>();

        foreach (var facts in _files.Enumerate(config.LocalFolderPath))
        {
            if (examined >= MostEntriesExamined)
            {
                truncated = true;

                break;
            }

            examined++;

            if (!pattern.Matches(facts.Name))
            {
                continue;
            }

            matches++;
            Remember(sample, facts);
        }

        return new FolderPreviewResult
        {
            LocalFolderPath = config.LocalFolderPath,
            FilePattern = pattern.Text,
            ListingStrategy = ListingStrategies.Enumerate,
            Examined = examined,
            Matches = matches,
            Truncated = truncated,
            Sample = sample.Select(facts => facts.Name).ToList(),
            // Told apart from "your pattern is wrong" by the counts, and said
            // in words as well because the tray shows the sentence first. The
            // seam answers a folder that is not there and a folder that is
            // empty the same way, so this says both.
            Problem = examined == 0
                ? "Nothing was found in this folder. Check that the path is right and that this "
                    + "machine can read it."
                : null,
        };
    }

    /// <summary>The direct-fetch answer: names built, then asked about.</summary>
    private FolderPreviewResult Fetching(StationLinkAppConfig config, DateTimeOffset now)
    {
        // No floor: the newest few hundred names is the question, not the
        // backlog. A preview that walked back to the collection start date
        // would take longer than the technician's patience on exactly the
        // stations this strategy exists for.
        var expected = ExpectedFiles.For(config, floor: null, now, MostNamesTried);

        if (expected.Problem is not null)
        {
            return Problem(config, expected.Problem);
        }

        var matches = 0;
        var sample = new List<FileFacts>();

        foreach (var name in expected.Names)
        {
            var facts = _files.Describe(config.LocalFolderPath, name);

            if (facts is null)
            {
                continue;
            }

            matches++;
            Remember(sample, facts.Value);
        }

        return new FolderPreviewResult
        {
            LocalFolderPath = config.LocalFolderPath,
            FilePattern = config.FilePattern ?? "",
            ListingStrategy = ListingStrategies.DirectFetch,
            // The names asked about, which is this strategy's equivalent of
            // the entries a walk went past.
            Examined = expected.Names.Count,
            Matches = matches,
            Truncated = expected.Truncated,
            Sample = sample.Select(facts => facts.Name).ToList(),
            Problem = matches == 0 && expected.Names.Count > 0
                ? $"None of the {expected.Names.Count} filenames these settings build are in this folder. "
                    + "Check the prefix, the extension and the datetime format against a real file."
                : null,
        };
    }

    /// <summary>
    /// Keep this file if it is among the newest seen so far.
    /// </summary>
    /// <remarks>
    /// Newest first because that is the order the cycle offers them in, and a
    /// sample that showed the oldest ten files of a year-old folder would
    /// leave a technician checking filenames from before the station existed.
    /// Kept by insertion into a list of ten rather than by sorting the
    /// matches, because on the folders that matter there are a hundred
    /// thousand matches and only ten of them are ever shown.
    /// </remarks>
    private static void Remember(List<FileFacts> sample, FileFacts facts)
    {
        var at = sample.FindIndex(held => held.WindowTimestamp < facts.WindowTimestamp);

        if (at < 0)
        {
            if (sample.Count < SampleSize)
            {
                sample.Add(facts);
            }

            return;
        }

        sample.Insert(at, facts);

        if (sample.Count > SampleSize)
        {
            sample.RemoveAt(sample.Count - 1);
        }
    }

    private static FolderPreviewResult Problem(StationLinkAppConfig config, string problem) => new()
    {
        LocalFolderPath = config.LocalFolderPath,
        FilePattern = config.FilePattern ?? "",
        ListingStrategy = config.ListingStrategy,
        Examined = 0,
        Matches = 0,
        Truncated = false,
        Sample = [],
        Problem = problem,
    };
}

/// <summary>What these settings would pick out, and what to say about it.</summary>
/// <param name="Examined">
/// Entries the walk went past, or names a DIRECT_FETCH station asked about.
/// Beside <see cref="Matches"/> it is the whole diagnosis: files here and
/// none matching is a pattern to fix, nothing here at all is a path to fix.
/// </param>
public sealed record FolderPreviewResult
{
    public required string LocalFolderPath { get; init; }

    public required string FilePattern { get; init; }

    public required string ListingStrategy { get; init; }

    public required int Matches { get; init; }

    public required int Examined { get; init; }

    /// <summary>True when the folder held more than one preview looks at.</summary>
    public required bool Truncated { get; init; }

    /// <summary>Up to ten matched filenames, newest first.</summary>
    public required IReadOnlyList<string> Sample { get; init; }

    /// <summary>The sentence the tray shows, or null when there is nothing to say.</summary>
    public string? Problem { get; init; }
}
