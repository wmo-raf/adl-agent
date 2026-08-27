namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// Where this machine's own record of what it did is kept, and how much of
/// it there may be.
/// </summary>
/// <remarks>
/// A subfolder of the state directory and never the state directory itself.
/// The state folder holds the device token, the configuration cache and the
/// sweep log -- the three things the agent must not lose -- and an eviction
/// routine that walked the same directory as those is one deletion away from
/// a machine somebody has to visit. So the eviction only ever looks in here,
/// and only ever at files it wrote itself.
/// <para>
/// The folder comes from <see cref="Platform.IHostLifecycle.StateDirectory"/>
/// and nothing here builds a path of its own above it, which is what keeps
/// this in the core with no platform conditional: <c>%ProgramData%</c> on
/// Windows and <c>/var/lib</c> on Linux are the head's business, and
/// "<c>logs</c> below it" is not.
/// </para>
/// </remarks>
public static class AgentLogs
{
    /// <summary>The folder, below the state directory.</summary>
    public const string FolderName = "logs";

    /// <summary>The cycle log's files: <c>cycle-20260827.jsonl</c>.</summary>
    public const string CycleLogName = "cycle";

    /// <summary>The general log's files: <c>agent-20260827.log</c>.</summary>
    public const string GeneralLogName = "agent";

    /// <summary>
    /// The cycle log's ceiling, in megabytes.
    /// </summary>
    /// <remarks>
    /// The larger of the two, because it is the one that answers questions.
    /// A day of an ordinary machine is 144 unit passes of a few hundred bytes
    /// each; the ceiling is for the machine having a bad week, and the promise
    /// it makes is the one a ministry's sysadmin needs in a sentence.
    /// </remarks>
    public const int CycleLogMegabytesDefault = 64;

    /// <summary>The general log's ceiling, in megabytes.</summary>
    public const int GeneralLogMegabytesDefault = 32;

    /// <summary>Where the logs are for a machine whose state is in <paramref name="stateDirectory"/>.</summary>
    public static string In(string stateDirectory) => Path.Combine(stateDirectory, FolderName);

    /// <summary>The suffix a rolled and compressed part carries.</summary>
    public const string CompressedSuffix = ".gz";

    /// <summary>
    /// What one log's files are called: <c>cycle-*.jsonl</c> and the
    /// <c>.gz</c> parts beside them.
    /// </summary>
    /// <remarks>
    /// Spelled once. Three places need it -- the writer, which deletes what it
    /// matches; the reader, which walks it newest first; and the diagnostics
    /// bundle, which takes the tail of it -- and a glob that had drifted in
    /// one of them would show up as a log that quietly reads or evicts the
    /// wrong set of files.
    /// </remarks>
    public static string Glob(string baseName, string extension) =>
        $"{baseName}-*{extension}*";

    /// <summary>Every file of one log in <paramref name="directory"/>.</summary>
    public static IEnumerable<string> FilesIn(
        string directory, string baseName, string extension) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, Glob(baseName, extension))
            : [];

    /// <summary>True when this file is a compressed part rather than a live one.</summary>
    public static bool IsCompressed(string path) =>
        path.EndsWith(CompressedSuffix, StringComparison.Ordinal);

    /// <summary>
    /// One of this folder's log files, opened for reading while it is being
    /// written to and possibly evicted.
    /// </summary>
    /// <remarks>
    /// <c>ReadWrite | Delete</c> because both happen: the newest file is being
    /// appended to by the queue right now, and the oldest may be trimmed away
    /// while a bundle is halfway through it. A reader that took an exclusive
    /// handle would make the log unreadable exactly while the agent was
    /// busiest -- and would stop the writer, which is worse.
    /// </remarks>
    public static TextReader OpenRead(string path)
    {
        var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        return IsCompressed(path)
            ? new StreamReader(
                new System.IO.Compression.GZipStream(
                    file, System.IO.Compression.CompressionMode.Decompress),
                System.Text.Encoding.UTF8)
            : new StreamReader(file, System.Text.Encoding.UTF8);
    }
}
