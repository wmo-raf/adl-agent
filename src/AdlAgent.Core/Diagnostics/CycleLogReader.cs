using System.Text.Json;
using AdlAgent.Core.Platform;
using AdlAgent.Core.Serialization;
using Microsoft.Extensions.Options;

namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// The cycle log, read back: recent passes, newest first.
/// </summary>
/// <remarks>
/// What the Check status… window shows and what the diagnostics bundle is
/// made of. Both want the same thing -- the last few passes touching one
/// station -- and both want it without reading a year of history to get it,
/// so the files are taken newest first and stopped at as soon as enough
/// records have been found.
/// <para>
/// A line that will not parse is skipped rather than thrown on. The newest
/// file is being appended to by another thread while this reads it, so its
/// last line is sometimes half a record; and a log that refused to be read
/// because of one bad line would be a log that is at its least useful
/// exactly when it is most needed.
/// </para>
/// </remarks>
public sealed class CycleLogReader
{
    /// <summary>
    /// The most records any one read will look at.
    /// </summary>
    /// <remarks>
    /// A bound on the work rather than on the answer: a station that appears
    /// in one pass in a thousand -- because the machine serves forty others
    /// -- would otherwise have this read the whole ceiling to find three
    /// rows for a window nobody is going to scroll.
    /// </remarks>
    public const int MostRecordsScanned = 20_000;

    private readonly string _directory;

    public CycleLogReader(IOptions<AgentOptions> options, IHostLifecycle host)
        : this(AgentLogs.In(options.Value.ResolveStateDirectory(host)))
    {
    }

    public CycleLogReader(string directory)
    {
        _directory = directory;
    }

    /// <summary>
    /// A page of rows, newest first, and how it was arrived at.
    /// </summary>
    /// <remarks>
    /// Filtering happens here rather than in whoever is drawing the table, so
    /// that a page is a page of matches and "load more" walks back through
    /// rows instead of through blank screens. It costs nothing: this is
    /// already reading and deserialising every record it passes.
    /// </remarks>
    public CyclePassIndex Index(CyclePassQuery query)
    {
        var rows = new List<CyclePassRow>();
        var walk = new Counter();

        foreach (var record in Walking(query.Skip, walk))
        {
            if (query.Matches(record))
            {
                rows.Add(CyclePassRow.Of(record, query.StationLinkId));

                if (rows.Count >= query.Most)
                {
                    break;
                }
            }
        }

        return new CyclePassIndex
        {
            Rows = rows,
            Exhausted = walk.Exhausted,
            Scanned = walk.Examined,
            Resume = query.Skip + walk.Examined,
        };
    }

    /// <summary>
    /// Every record this query matches, newest first, up to
    /// <see cref="CyclePassQuery.Most"/>.
    /// </summary>
    /// <remarks>
    /// The whole records, for the diagnostics bundle -- which renders them as
    /// text and is not on a wire, so the file detail that the index leaves out
    /// is exactly what it is there to carry.
    /// </remarks>
    public IReadOnlyList<CycleRecord> Recent(CyclePassQuery query)
    {
        var found = new List<CycleRecord>();

        foreach (var record in Walking(query.Skip, new Counter()))
        {
            if (!query.Matches(record))
            {
                continue;
            }

            found.Add(record);

            if (found.Count >= query.Most)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>
    /// Every record in this log, newest first, past the first
    /// <paramref name="skip"/> and bounded by
    /// <see cref="MostRecordsScanned"/>.
    /// </summary>
    /// <remarks>
    /// One walk, consumed by both the index and the bundle. They had a copy
    /// each, and the copies had already drifted: one counted its budget before
    /// deciding whether a record matched and the other after, so the same log
    /// gave up in two different places depending on which question was asked.
    /// <para>
    /// Skipped records count against the budget. Without that, a deep page
    /// would walk the whole log to reach its own starting point -- against a
    /// pipe the client abandons after three seconds.
    /// </para>
    /// </remarks>
    /// <param name="counter">
    /// Filled in as the walk goes, and read after it stops. The caller owns it
    /// because a caller that breaks out early -- which every one of them does
    /// -- still has to know how far the walk got and whether it ran out.
    /// </param>
    private IEnumerable<CycleRecord> Walking(int skip, Counter counter)
    {
        foreach (var path in Newest())
        {
            foreach (var line in Backwards(path))
            {
                if (Parse(line) is not { } record)
                {
                    continue;
                }

                // Counted after parsing, so the budget bounds records rather
                // than lines: a file's blank tail would otherwise spend it.
                counter.Examined++;

                if (counter.Examined > skip)
                {
                    yield return record;
                }

                if (counter.Examined - skip >= MostRecordsScanned)
                {
                    // Stopped looking, which is not the same as having found
                    // everything -- and the difference is the whole reason the
                    // answer carries three facts instead of a list.
                    yield break;
                }
            }
        }

        counter.Exhausted = true;
    }

    /// <summary>What a walk got through, readable after it has stopped.</summary>
    private sealed class Counter
    {
        public int Examined { get; set; }

        public bool Exhausted { get; set; }
    }

    /// <summary>
    /// One pass, named by when it started and the folder it walked.
    /// </summary>
    /// <remarks>
    /// A natural key rather than an id, because both halves are already in
    /// the record and in the row that asks for it. It is unique: two units
    /// never share a folder -- that is what grouping stations by the folders
    /// they share is for -- and one unit cannot pass twice at once, because
    /// the cycle claims its stations before it reads anything.
    /// <para>
    /// The exception is a unit with no folder at all, and it is harmless. A
    /// station the scan turned away has nothing to join it to anything, so it
    /// lands in a unit by itself with an empty folder and a sentence -- and
    /// such a record has no folders walked and no files, so the row asking for
    /// its detail already holds everything there is.
    /// </para>
    /// <para>
    /// The whole log is searched rather than the part of it near the moment
    /// wanted, and that is not laziness. Units run several at a time and a
    /// record is written when its unit <em>finishes</em> while its timestamp
    /// is when the unit <em>started</em>, so records are not in timestamp
    /// order and there is no point at which this could stop early and be
    /// right. What bounds it is the same budget every other read has.
    /// </para>
    /// </remarks>
    /// <returns>The record, or <c>null</c> if it has been evicted since.</returns>
    public CycleRecord? One(DateTimeOffset at, string unit)
    {
        foreach (var record in Walking(0, new Counter()))
        {
            if (record.At == at && string.Equals(record.Unit, unit, StringComparison.Ordinal))
            {
                return record;
            }
        }

        return null;
    }

    /// <summary>
    /// This log's files, newest first.
    /// </summary>
    /// <remarks>
    /// By the name and not by the file's timestamp, for the same reason the
    /// writer evicts by the name: compressing a part rewrites its stamp, so a
    /// day gzipped late would sort as the newest thing in the folder and a
    /// window asking for recent passes would be handed last week's.
    /// </remarks>
    private IEnumerable<string> Newest() =>
        AgentLogs.FilesIn(_directory, AgentLogs.CycleLogName, CycleLog.Extension)
            .OrderByDescending(path => path, StringComparer.Ordinal);

    /// <summary>
    /// One file's lines, last first.
    /// </summary>
    /// <remarks>
    /// Read whole and reversed rather than seeked backwards. A file here is
    /// at most a rolled part -- an eighth of the ceiling, and gzipped, which
    /// has to be decompressed from the front anyway -- and a backwards seek
    /// over a stream being appended to by another thread is a great deal of
    /// care for a window somebody has open for thirty seconds.
    /// </remarks>
    private static IEnumerable<string> Backwards(string path)
    {
        List<string> lines;

        try
        {
            lines = Read(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException)
        {
            // A part half-written when the power went, or one this process
            // does not have rights to. The rest of the log still reads.
            yield break;
        }

        for (var index = lines.Count - 1; index >= 0; index--)
        {
            yield return lines[index];
        }
    }

    private static List<string> Read(string path)
    {
        using var reader = AgentLogs.OpenRead(path);

        var lines = new List<string>();

        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static CycleRecord? Parse(string line)
    {
        try
        {
            var record = JsonSerializer.Deserialize<CycleRecord>(line, AgentJson.Options);

            // A record with no stations in it is one of the queue's own
            // notes about what it dropped, or a line from a version that
            // wrote something else. Neither is a pass.
            return record?.Stations is null ? null : record;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
