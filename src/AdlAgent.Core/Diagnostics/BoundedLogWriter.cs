using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// One log, appended to, rolled, compressed and kept under a hard byte
/// ceiling.
/// </summary>
/// <remarks>
/// The ceiling is a total and not an age window, because a bad week is a
/// hundred times a good one and the promise that has to be makeable to a
/// ministry's system administrator is one sentence: <em>this folder never
/// exceeds 96 MB</em>. An age window makes no such promise -- it is a
/// promise about the calendar and a guess about the disk.
/// <para>
/// Today's file is plain and appendable and everything already rolled is
/// gzipped. These records are enormously repetitive -- the same station
/// names, the same verbs, the same folder paths, 144 times a day -- so gzip
/// returns roughly 10-15x on the archive, which turns the ceiling from
/// months of history into years for free. The active file stays plain
/// because a technician standing at the machine should be able to open it.
/// </para>
/// <para>
/// A day is not the only thing that rolls it. A single pathological pass --
/// a share that unmounted and failed every file in a folder, a first bind
/// uploading for hours -- can write more in one cycle than the whole
/// ceiling, and a writer that only rolled at midnight would hold all of that
/// uncompressed and unevictable. So the active file also rolls once it
/// passes <see cref="PartBytes"/>, which is what makes the ceiling hold
/// <em>during</em> a bad cycle rather than the morning after it.
/// </para>
/// <para>
/// Not thread-safe, and deliberately not: one <see cref="BackgroundLogQueue"/>
/// owns one of these and is its only caller. Guarding it here would be
/// guarding against a caller that does not exist, and would invite one.
/// </para>
/// </remarks>
public sealed class BoundedLogWriter
{
    /// <summary>
    /// The longest single line that will be written.
    /// </summary>
    /// <remarks>
    /// Far larger than any record this agent builds -- the file detail is
    /// bounded long before it gets here -- and present so that the ceiling
    /// cannot be defeated by one enormous line, which is the one shape
    /// rolling by size cannot answer.
    /// </remarks>
    public const int MaxLineBytes = 256 * 1024;

    private readonly string _directory;
    private readonly string _baseName;
    private readonly string _extension;
    private readonly long _ceilingBytes;
    private readonly TimeProvider _time;

    private string? _activePath;
    private DateOnly _activeDay;
    private long _activeBytes;

    /// <summary>
    /// What everything this log has already rolled occupies.
    /// </summary>
    /// <remarks>
    /// Kept rather than measured, and that is a performance decision with a
    /// correctness consequence, so it is worth stating. Measuring meant
    /// enumerating the folder and stat-ing every file in it on every single
    /// line written -- which on a machine an administrator has put on
    /// <c>Debug</c> for a day is thousands of syscalls a minute, on the queue
    /// whose falling behind is how records get dropped.
    /// <para>
    /// The set only changes when this rolls or trims, and both recompute it.
    /// Between them the total is this plus the active file, which is exact --
    /// so the ceiling still holds to within the one line being written, rather
    /// than to within whatever interval a cheaper check would have skipped.
    /// </para>
    /// </remarks>
    private long _archivedBytes;

    /// <param name="baseName">
    /// What this log's files are called before the date: <c>cycle</c> gives
    /// <c>cycle-20260827.jsonl</c> and <c>cycle-20260826-001.jsonl.gz</c>.
    /// </param>
    /// <param name="ceilingBytes">
    /// The most this log may occupy, ever, counting what is compressed. Its
    /// own files only: two logs in one folder hold two independent ceilings,
    /// which is what stops a chatty subsystem evicting cycle history.
    /// </param>
    public BoundedLogWriter(
        string directory, string baseName, string extension, long ceilingBytes, TimeProvider time)
    {
        _directory = directory;
        _baseName = baseName;
        _extension = extension;
        _ceilingBytes = Math.Max(ceilingBytes, MinimumCeilingBytes);
        _time = time;
    }

    /// <summary>
    /// How big the active file may get before it is rolled and compressed.
    /// </summary>
    /// <remarks>
    /// A fraction of the ceiling rather than a number, so that the
    /// uncompressed part of the folder is always a small share of what was
    /// promised. It is also the amount of history a machine that dies
    /// mid-cycle keeps at full size, which is the other reason not to make it
    /// large.
    /// </remarks>
    public long PartBytes => Math.Max(1024 * 1024, _ceilingBytes / 8);

    /// <summary>
    /// The floor under the ceiling.
    /// </summary>
    /// <remarks>
    /// A ceiling below this could not hold one rolled part, so the log would
    /// spend its life deleting what it had just written. A machine whose
    /// settings file says <c>CycleLogMegabytes=0</c> gets this instead of a
    /// log that does not work.
    /// </remarks>
    public const long MinimumCeilingBytes = 4 * 1024 * 1024;

    /// <summary>Append one record, and keep the folder inside its ceiling.</summary>
    /// <remarks>
    /// Every failure here is swallowed. A disk that is full, a folder an
    /// administrator has re-permissioned, an antivirus holding the file open
    /// -- none of them is a reason for a country's observations to stop
    /// moving, which is what letting an exception out of a log write would
    /// eventually mean.
    /// </remarks>
    public void Write(string text)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Trimmed(text) + "\n");

            Prepare(bytes.Length);

            if (_activePath is null)
            {
                // There is nowhere to write this that would be safe to make.
                return;
            }

            using (var stream = new FileStream(
                _activePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes);
            }

            _activeBytes += bytes.Length;

            if (_archivedBytes + _activeBytes > _ceilingBytes)
            {
                Trim();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            // Nowhere to report this to: the thing that reports is this. The
            // active file is forgotten so the next write re-opens it, which
            // is what recovers a machine whose share came back.
            _activePath = null;
        }
    }

    /// <summary>Every file this log owns, oldest first.</summary>
    /// <remarks>
    /// Its own and nothing else's, which is the whole of the eviction safety
    /// argument: the pattern is this log's base name, the folder is the logs
    /// folder, and the state directory above it is never enumerated.
    /// </remarks>
    public IReadOnlyList<string> Files()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(_directory, $"{_baseName}-*{_extension}*")
            .Where(Owns)
            .OrderBy(File.GetLastWriteTimeUtc)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>How many bytes this log currently occupies.</summary>
    public long Bytes() => Files().Sum(Length);

    /// <summary>
    /// The active file, rolled and compressed now.
    /// </summary>
    /// <remarks>
    /// Public because a technician saving a diagnostics bundle wants the
    /// records of the pass that is still being written, and because a test
    /// that has just written a day's worth should be able to see what the
    /// day after would.
    /// </remarks>
    public void Roll()
    {
        if (_activePath is null || !File.Exists(_activePath))
        {
            _activePath = null;

            return;
        }

        Compress(_activePath, _activeDay);

        _activePath = null;
        _activeBytes = 0;
        _archivedBytes = Bytes();
    }

    /// <summary>
    /// Make sure there is an active file with room in it for what is about to
    /// be written.
    /// </summary>
    private void Prepare(int incoming)
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

        if (_activePath is not null && (_activeDay != today || _activeBytes + incoming > PartBytes))
        {
            Roll();
        }

        if (_activePath is not null && File.Exists(_activePath))
        {
            return;
        }

        // The logs folder is made; the state directory above it never is.
        // That folder's permissions are replaced by the installer with SYSTEM
        // and Administrators, because the device token is stored in it in the
        // clear -- and a directory tree created here instead would inherit
        // whatever %ProgramData% grants, so the next pairing would put a
        // credential somewhere every local account can read it. A machine that
        // is not an installed agent therefore keeps no log, which is the right
        // way round: it has nothing to keep one about.
        if (Path.GetDirectoryName(_directory) is { Length: > 0 } parent &&
            !Directory.Exists(parent))
        {
            return;
        }

        Directory.CreateDirectory(_directory);

        // A plain file from a day this process did not see the end of: the
        // service was stopped overnight, or the machine lost power. It is a
        // rolled day now, and compressing it here is what keeps "rolled days
        // are gzipped" true across a restart.
        foreach (var stale in Plain().Where(path => !IsToday(path, today)))
        {
            Compress(stale, DayOf(stale) ?? today);
        }

        _activeDay = today;
        _activePath = Path.Combine(
            _directory, $"{_baseName}-{today:yyyyMMdd}{_extension}");
        _activeBytes = Length(_activePath);
        _archivedBytes = Bytes() - _activeBytes;
    }

    /// <summary>
    /// Delete the oldest of this log's files until it is inside its ceiling.
    /// </summary>
    /// <remarks>
    /// Oldest first, and whole files: what expires is whole days and whole
    /// parts of a day, so a reader never meets half a story. The active file
    /// is never one of the candidates -- it is the one being written -- and
    /// it cannot be the reason the ceiling is breached, because it is rolled
    /// at a fraction of it.
    /// </remarks>
    private void Trim()
    {
        var files = Files();
        var total = files.Sum(Length);

        foreach (var path in files)
        {
            if (total <= _ceilingBytes)
            {
                break;
            }

            if (string.Equals(path, _activePath, StringComparison.Ordinal))
            {
                continue;
            }

            var length = Length(path);

            try
            {
                File.Delete(path);

                total -= length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Somebody has it open -- a technician reading it, or the
                // bundle being written. It will be the oldest again next
                // time.
            }
        }

        _archivedBytes = Math.Max(0, total - _activeBytes);
    }

    /// <summary>
    /// <paramref name="path"/> as a gzipped part of <paramref name="day"/>.
    /// </summary>
    /// <remarks>
    /// Written to a temporary name and moved into place, so a compression
    /// interrupted by a power cut leaves the plain file rather than a
    /// truncated archive -- and the next start compresses it again.
    /// </remarks>
    private void Compress(string path, DateOnly day)
    {
        var target = NextPart(day);
        var working = target + ".partial";

        try
        {
            using (var source = File.OpenRead(path))
            using (var destination = File.Create(working))
            using (var gzip = new GZipStream(destination, CompressionLevel.Optimal))
            {
                source.CopyTo(gzip);
            }

            File.Move(working, target, overwrite: true);
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The plain file stays where it is and is tried again next time.
            // Left uncompressed it still counts against the ceiling, so this
            // cannot become a way to fill a disk.
            try
            {
                File.Delete(working);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The next unused part name for a day.</summary>
    private string NextPart(DateOnly day)
    {
        for (var sequence = 1; ; sequence++)
        {
            var candidate = Path.Combine(
                _directory,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{_baseName}-{day:yyyyMMdd}-{sequence:D3}{_extension}.gz"));

            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private IEnumerable<string> Plain() =>
        Directory.EnumerateFiles(_directory, $"{_baseName}-*{_extension}").Where(Owns);

    private bool IsToday(string path, DateOnly today) => DayOf(path) == today;

    /// <summary>
    /// The day in a file's name, or <c>null</c> when it does not carry one.
    /// </summary>
    private static DateOnly? DayOf(string path)
    {
        var name = Path.GetFileName(path);
        var dash = name.IndexOf('-');

        if (dash < 0 || name.Length < dash + 9)
        {
            return null;
        }

        return DateOnly.TryParseExact(
            name.Substring(dash + 1, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var day)
            ? day
            : null;
    }

    /// <summary>
    /// True when this file is one of ours.
    /// </summary>
    /// <remarks>
    /// The glob alone is not enough: <c>agent-*</c> matches <c>agent.ini</c>
    /// nowhere near here but would match a hypothetical <c>agent-notes.txt</c>
    /// somebody dropped in the folder, and this routine deletes what it
    /// matches. So the name has to be this log's base, a dash, eight digits
    /// of date, and then only our own suffixes.
    /// </remarks>
    private bool Owns(string path)
    {
        if (DayOf(path) is null || !Path.GetFileName(path).StartsWith($"{_baseName}-", StringComparison.Ordinal))
        {
            return false;
        }

        var name = Path.GetFileName(path);

        return name.EndsWith(_extension, StringComparison.Ordinal)
            || name.EndsWith(_extension + ".gz", StringComparison.Ordinal);
    }

    private static long Length(string path)
    {
        try
        {
            var file = new FileInfo(path);

            return file.Exists ? file.Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The text, capped.
    /// </summary>
    /// <remarks>
    /// Only capped. Newlines are left alone on purpose: the cycle log cannot
    /// contain one -- compact JSON escapes them -- and the general log is
    /// full of them, because an exception's stack trace is the thing somebody
    /// opens that file to read.
    /// </remarks>
    private static string Trimmed(string text) =>
        text.Length <= MaxLineBytes ? text : text[..MaxLineBytes] + "…";
}
