using AdlAgent.Core.Api;
using AdlAgent.Core.Control;
using AdlAgent.Core.Platform;
using AdlAgent.Core.State;

namespace AdlAgent.TestSupport;

/// <summary>
/// The four platform seams, faked.
/// </summary>
/// <remarks>
/// These exist so that a test can describe a filesystem it is not on. The
/// agent's hardest behaviour is windowing and readiness, and the cases that
/// matter are Windows cases -- a file copied in with an old last-write time,
/// a vendor process holding its output open -- which a Linux CI runner
/// cannot produce. Behind the seams that stops mattering: the test states the
/// facts, and the core does what it would do on the real machine.
/// </remarks>
public sealed class FakeHostLifecycle : IHostLifecycle
{
    public string PlatformDescription { get; set; } = "Microsoft Windows 10.0.20348";

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Parse("2026-08-21T08:00:00Z");

    public string StateDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "adl-agent-tests");
}

/// <summary>
/// A filesystem stated rather than created: folders, filenames and the one
/// timestamp the window is measured against.
/// </summary>
public sealed class FakeFileMetadataSource : IFileMetadataSource
{
    private readonly Dictionary<string, List<FileFacts>> _folders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Put a file in a folder, with the windowing timestamp it should have.</summary>
    public FakeFileMetadataSource Add(
        string folder, string name, DateTimeOffset windowTimestamp, long length = 1024)
    {
        var path = folder.TrimEnd('/', '\\') + PathSeparator(folder) + name;

        if (!_folders.TryGetValue(folder, out var files))
        {
            files = [];
            _folders[folder] = files;
        }

        files.RemoveAll(file => file.Name == name);
        files.Add(new FileFacts(path, name, length, windowTimestamp));

        return this;
    }

    public IEnumerable<FileFacts> Enumerate(string folderPath) =>
        _folders.TryGetValue(folderPath, out var files) ? files.ToList() : [];

    public FileFacts? Describe(string path)
    {
        foreach (var files in _folders.Values)
        {
            foreach (var file in files)
            {
                if (string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }

        return null;
    }

    private static char PathSeparator(string folder) => folder.Contains('\\') ? '\\' : '/';
}

/// <summary>
/// A readiness probe with the platform's half of the judgement made explicit.
/// </summary>
/// <remarks>
/// <see cref="LockedPaths"/> is how a test says "a vendor process is holding
/// this open" on a machine where nothing is. Leaving it empty and setting
/// <see cref="ObservesLocks"/> to false gives the Linux behaviour, where the
/// stability window is all there is -- which is the comparison the
/// designed-for-later Linux head needs to be able to make.
/// </remarks>
public sealed class FakeFileReadinessProbe : IFileReadinessProbe
{
    public bool ObservesLocks { get; set; } = true;

    public HashSet<string> LockedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsReadyToRead(FileFacts file, TimeSpan stabilityWindow, DateTimeOffset now)
    {
        if (stabilityWindow > TimeSpan.Zero && now - file.WindowTimestamp < stabilityWindow)
        {
            return false;
        }

        return !ObservesLocks || !LockedPaths.Contains(file.Path);
    }
}

/// <summary>
/// A control surface with no transport: the test is the client.
/// </summary>
public sealed class FakeControlSurface : IControlSurface
{
    private readonly TaskCompletionSource<ControlRequestHandler> _serving =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task ServeAsync(ControlRequestHandler handler, CancellationToken stoppingToken)
    {
        _serving.TrySetResult(handler);

        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>Say something to the agent, as the tray would.</summary>
    public async Task<ControlResponse> SendAsync(
        ControlRequest request, CancellationToken cancellationToken = default)
    {
        var handler = await _serving.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);

        return await handler(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The state store with the disk taken out.</summary>
public sealed class InMemoryAgentStateStore : IAgentStateStore
{
    private AgentState _state = new();
    private CachedConfiguration? _config;

    /// <summary>How many times state has been written. A restart is a new store.</summary>
    public int Writes { get; private set; }

    public AgentState Load() => _state;

    public void Save(AgentState state)
    {
        _state = state;
        Writes++;
    }

    public CachedConfiguration? LoadConfig() => _config;

    public void SaveConfig(SyncResponse config, DateTimeOffset fetchedAt) =>
        _config = new CachedConfiguration { FetchedAt = fetchedAt, Config = config };

    /// <summary>Put a cached configuration in place, as a previous run would have left it.</summary>
    public void Seed(SyncResponse config, DateTimeOffset fetchedAt) => SaveConfig(config, fetchedAt);
}

/// <summary>
/// How each platform picks the timestamp a file is windowed on.
/// </summary>
/// <remarks>
/// Stated here so a test can say which platform's semantics it is describing
/// and read as if it were running there. The Windows rule is not an
/// implementation detail worth hiding: it is the reason a file copied into a
/// folder today, carrying last week's last-write time, is still offered.
/// </remarks>
public static class PlatformWindowing
{
    /// <summary>Windows: the later of last-write and creation.</summary>
    public static DateTimeOffset WindowsLike(DateTimeOffset lastWrite, DateTimeOffset creation) =>
        lastWrite > creation ? lastWrite : creation;

    /// <summary>Linux: birth time where the filesystem has it, else last-write.</summary>
    public static DateTimeOffset LinuxLike(DateTimeOffset lastWrite, DateTimeOffset? birth = null) =>
        birth ?? lastWrite;
}
