using System.Text.Json;
using AdlAgent.Core.Api;
using AdlAgent.Core.Platform;
using AdlAgent.Core.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdlAgent.Core.State;

/// <summary>
/// Three JSON files in the directory the host handed over.
/// </summary>
/// <remarks>
/// Separate files rather than one because they fail differently. A
/// configuration cache that gets truncated by a power cut is a cache miss --
/// the agent syncs and moves on; a lost sweep log costs one extra sweep. The
/// token being lost is a machine that has to be re-paired by someone in the
/// building. Keeping them apart means a bad write to a noisy one can never
/// take the precious one with it.
/// <para>
/// Each is written through <see cref="AtomicFile"/>, so a crash mid-write
/// leaves the previous contents rather than half of the new ones. Country
/// servers lose power; a token file that survives the outage as a truncated
/// fragment is a machine somebody has to visit.
/// </para>
/// </remarks>
public sealed class FileAgentStateStore : IAgentStateStore
{
    private const string StateFileName = "state.json";
    private const string ConfigCacheFileName = "config-cache.json";
    private const string SweepLogFileName = "sweeps.json";

    private readonly string _directory;
    private readonly ILogger<FileAgentStateStore> _logger;

    /// <summary>
    /// Held for reads as well as writes: a read that caught its own writer
    /// mid-move would report the file as absent, and "absent" for the token
    /// file means an unpaired machine.
    /// </summary>
    private readonly Lock _fileLock = new();

    public FileAgentStateStore(
        IOptions<AgentOptions> options,
        IHostLifecycle host,
        ILogger<FileAgentStateStore> logger)
    {
        _directory = string.IsNullOrWhiteSpace(options.Value.StateDirectory)
            ? host.StateDirectory
            : options.Value.StateDirectory!;
        _logger = logger;
    }

    public AgentState Load() => Read<AgentState>(StateFileName) ?? new AgentState();

    public void Save(AgentState state) => Write(StateFileName, state);

    public CachedConfiguration? LoadConfig() => Read<CachedConfiguration>(ConfigCacheFileName);

    public void SaveConfig(SyncResponse config, DateTimeOffset fetchedAt) =>
        Write(ConfigCacheFileName, new CachedConfiguration
        {
            FetchedAt = fetchedAt,
            Config = config,
        });

    public SweepLog LoadSweeps() => Read<SweepLog>(SweepLogFileName) ?? new SweepLog();

    public void SaveSweeps(SweepLog sweeps) => Write(SweepLogFileName, sweeps);

    /// <summary>
    /// Delete all three files, so the machine comes back knowing only where
    /// it reports.
    /// </summary>
    /// <remarks>
    /// Deleted rather than written empty. An absent token file is already how
    /// an unpaired machine looks to <see cref="Load"/>, and an absent cache
    /// is already a cache miss -- so this leaves the disk in a state the rest
    /// of the agent has always known how to read, rather than in a new one
    /// that would need its own handling.
    /// </remarks>
    public void ForgetInstance()
    {
        lock (_fileLock)
        {
            foreach (var fileName in new[] { StateFileName, ConfigCacheFileName, SweepLogFileName })
            {
                File.Delete(Path.Combine(_directory, fileName));
            }
        }
    }

    private T? Read<T>(string fileName) where T : class
    {
        var path = Path.Combine(_directory, fileName);

        try
        {
            lock (_fileLock)
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), AgentJson.Options);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable is treated as absent on purpose. A machine whose
            // cache file was corrupted should sync and carry on, not refuse
            // to start; and a machine whose token file is unreadable has to
            // be re-paired either way, which is what "absent" leads to.
            _logger.LogWarning(exception, "Could not read {Path}; treating it as not there.", path);

            return null;
        }
    }

    private void Write<T>(string fileName, T value)
    {
        lock (_fileLock)
        {
            Directory.CreateDirectory(_directory);

            AtomicFile.Write(
                Path.Combine(_directory, fileName),
                JsonSerializer.Serialize(value, AgentJson.Options));
        }
    }
}
