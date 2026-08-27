using System.IO.Compression;
using System.Text;
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
    /// The most recent passes, newest first.
    /// </summary>
    /// <param name="stationLinkId">
    /// Only the passes this station was in, or <c>null</c> for all of them.
    /// A station rather than a unit, because that is the question somebody
    /// standing at the machine is asking -- and a station's unit is whatever
    /// it happens to share a folder with, which is not a thing anybody knows
    /// the name of.
    /// </param>
    public IReadOnlyList<CycleRecord> Recent(int most, long? stationLinkId = null)
    {
        var found = new List<CycleRecord>();
        var scanned = 0;

        foreach (var path in Newest())
        {
            foreach (var line in Backwards(path))
            {
                if (++scanned > MostRecordsScanned)
                {
                    return found;
                }

                if (Parse(line) is not { } record)
                {
                    continue;
                }

                if (stationLinkId is not null &&
                    !record.Stations.Any(station => station.StationLinkId == stationLinkId))
                {
                    continue;
                }

                found.Add(record);

                if (found.Count >= most)
                {
                    return found;
                }
            }
        }

        return found;
    }

    /// <summary>This log's files, newest first.</summary>
    private IEnumerable<string> Newest()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(_directory, $"{AgentLogs.CycleLogName}-*{CycleLog.Extension}*")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(path => path, StringComparer.Ordinal);
    }

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
        using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // ReadWrite and Delete: the newest file is being appended to right
        // now, and the oldest may be evicted while this is reading it.
        using var reader = path.EndsWith(".gz", StringComparison.Ordinal)
            ? new StreamReader(new GZipStream(file, CompressionMode.Decompress), Encoding.UTF8)
            : new StreamReader(file, Encoding.UTF8);

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
