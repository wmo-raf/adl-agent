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
}
