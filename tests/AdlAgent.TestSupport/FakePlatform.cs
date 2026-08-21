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
/// A vendor folder: real bytes on this machine, under the folder path and the
/// timestamps the test says they have.
/// </summary>
/// <remarks>
/// Both halves matter. The bytes are real because everything downstream of
/// the seam does real work on them -- hashing, streaming an upload, a server
/// checking the digest -- and a fake that only stated sizes would leave all
/// of that untested. The <em>facts</em> are stated because the cases that
/// matter belong to another operating system: a file copied in today carrying
/// last week's last-write time is a Windows story, and a Linux CI runner
/// cannot produce one.
/// <para>
/// So a test says where the vendor writes ("C:\\VendorData\\Garissa") and what
/// it wrote, and this puts the bytes somewhere it is allowed to put them. The
/// core never builds a path of its own under ENUMERATE -- it opens what the
/// seam handed it -- so the difference does not reach it.
/// </para>
/// </remarks>
public sealed class FakeFileMetadataSource : IFileMetadataSource, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("adl-agent-vendor").FullName;
    private readonly Dictionary<string, Folder> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>
    /// How many times each folder has been walked, by folder path.
    /// </summary>
    /// <remarks>
    /// The one number that proves the rule the spec cares most about: one
    /// enumeration per distinct folder per cycle, however many stations share
    /// it. On the folders this product exists for, a second walk is not a
    /// tidiness problem.
    /// </remarks>
    public IReadOnlyDictionary<string, int> Enumerations
    {
        get
        {
            lock (_gate)
            {
                return _folders.ToDictionary(
                    entry => entry.Key, entry => entry.Value.Walks, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public int EnumerationsOf(string folder) =>
        Enumerations.TryGetValue(folder, out var walks) ? walks : 0;

    /// <summary>Write a file into a folder, with the time the window sees.</summary>
    public FakeFileMetadataSource Add(
        string folder, string name, DateTimeOffset windowTimestamp, string contents)
    {
        var path = Place(folder, name);

        File.WriteAllText(path, contents);

        return Record(folder, name, path, windowTimestamp);
    }

    /// <summary>The same, for a test that cares about the size and not the bytes.</summary>
    public FakeFileMetadataSource Add(
        string folder, string name, DateTimeOffset windowTimestamp, long length = 1024)
    {
        var path = Place(folder, name);

        // Distinct per file: two files of the same length that hashed alike
        // would make "ADL already holds this" and "this is a different file"
        // indistinguishable in a test.
        var filler = new byte[length];

        for (var index = 0; index < filler.Length; index++)
        {
            filler[index] = (byte)((name.GetHashCode(StringComparison.Ordinal) + index) & 0xFF);
        }

        File.WriteAllBytes(path, filler);

        return Record(folder, name, path, windowTimestamp);
    }

    /// <summary>
    /// Append to a file, the way a logger fills a daily CSV.
    /// </summary>
    /// <remarks>
    /// The timestamp moves with the write, because on a real machine it
    /// would: an append bumps last-write, which is what puts a grown file
    /// back inside the candidate window (story 14).
    /// </remarks>
    public FakeFileMetadataSource Append(
        string folder, string name, string more, DateTimeOffset writtenAt)
    {
        var path = Place(folder, name);

        File.AppendAllText(path, more);

        return Record(folder, name, path, writtenAt);
    }

    /// <summary>
    /// Say a file is in the folder without ever writing it.
    /// </summary>
    /// <remarks>
    /// For the files a cycle must never open: the hundreds of thousands
    /// sitting below a station's watermark, whose whole point is that the
    /// walk sees them and nothing else does. If the agent were to read one,
    /// it would not find it -- which is the assertion.
    /// </remarks>
    public FakeFileMetadataSource State(
        string folder, string name, DateTimeOffset windowTimestamp, long length = 1024)
    {
        var path = Place(folder, name);

        lock (_gate)
        {
            _folders[folder].Entries[name] = new FileFacts(path, name, length, windowTimestamp);
        }

        return this;
    }

    /// <summary>Take a file away, as a vendor's archiving job would.</summary>
    public void Remove(string folder, string name)
    {
        lock (_gate)
        {
            if (_folders.TryGetValue(folder, out var files) && files.Entries.Remove(name))
            {
                File.Delete(Path.Combine(files.Directory, name));
            }
        }
    }

    /// <summary>Where this file really is, which is what the seam hands over.</summary>
    public string PathOf(string folder, string name)
    {
        lock (_gate)
        {
            return _folders.TryGetValue(folder, out var files) && files.Entries.TryGetValue(name, out var facts)
                ? facts.Path
                : Path.Combine(DirectoryFor(folder), name);
        }
    }

    public IEnumerable<FileFacts> Enumerate(string folderPath)
    {
        lock (_gate)
        {
            if (!_folders.TryGetValue(folderPath, out var files))
            {
                return [];
            }

            files.Walks++;

            return files.Entries.Values.ToList();
        }
    }

    public FileFacts? Describe(string path)
    {
        lock (_gate)
        {
            foreach (var folder in _folders.Values)
            {
                foreach (var facts in folder.Entries.Values)
                {
                    if (string.Equals(facts.Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        return facts;
                    }
                }
            }
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Place(string folder, string name) => Path.Combine(DirectoryFor(folder), name);

    private string DirectoryFor(string folder)
    {
        lock (_gate)
        {
            if (!_folders.TryGetValue(folder, out var files))
            {
                // Named after the folder rather than its path, which may well
                // be a Windows one on a machine that has no C: drive.
                files = new Folder(Path.Combine(_root, _folders.Count.ToString()));
                Directory.CreateDirectory(files.Directory);
                _folders[folder] = files;
            }

            return files.Directory;
        }
    }

    private FakeFileMetadataSource Record(
        string folder, string name, string path, DateTimeOffset windowTimestamp)
    {
        lock (_gate)
        {
            _folders[folder].Entries[name] =
                new FileFacts(path, name, new FileInfo(path).Length, windowTimestamp);
        }

        return this;
    }

    private sealed class Folder(string directory)
    {
        public string Directory { get; } = directory;

        public Dictionary<string, FileFacts> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Walks { get; set; }
    }
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
