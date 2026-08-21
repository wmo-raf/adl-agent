using System.Text.Json;
using AdlAgent.Core.Api;
using AdlAgent.Core.Platform;
using AdlAgent.Core.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdlAgent.Core.State;

/// <summary>
/// Two JSON files in the directory the host handed over.
/// </summary>
/// <remarks>
/// Two files rather than one because they fail differently. A configuration
/// cache that gets truncated by a power cut is a cache miss -- the agent
/// syncs and moves on. The token being lost is a machine that has to be
/// re-paired by someone in the building. Keeping them apart means a bad
/// write to the noisy one can never take the precious one with it.
/// <para>
/// Both are flushed to the disk under a temporary name and then moved into
/// place, so a crash mid-write leaves the previous contents rather than half
/// of the new ones. Country servers lose power; a token file that survives
/// the outage as a truncated fragment is a machine somebody has to visit.
/// </para>
/// </remarks>
public sealed class FileAgentStateStore : IAgentStateStore
{
    private const string StateFileName = "state.json";
    private const string ConfigCacheFileName = "config-cache.json";

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

            var path = Path.Combine(_directory, fileName);
            var temporary = path + ".tmp";

            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file))
            {
                writer.Write(JsonSerializer.Serialize(value, AgentJson.Options));
                writer.Flush();

                // On the disk, not merely in the operating system's hands,
                // before anything is moved over the file that is still good.
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
    }
}
