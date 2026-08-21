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
/// Both are written to a temporary file and moved into place, so a crash
/// mid-write leaves the previous contents rather than half of the new ones.
/// </para>
/// </remarks>
public sealed class FileAgentStateStore : IAgentStateStore
{
    private const string StateFileName = "state.json";
    private const string ConfigCacheFileName = "config-cache.json";

    private readonly string _directory;
    private readonly ILogger<FileAgentStateStore> _logger;
    private readonly Lock _writeLock = new();

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
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), AgentJson.Options);
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
        lock (_writeLock)
        {
            Directory.CreateDirectory(_directory);

            var path = Path.Combine(_directory, fileName);
            var temporary = path + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(value, AgentJson.Options));
            File.Move(temporary, path, overwrite: true);
        }
    }
}
