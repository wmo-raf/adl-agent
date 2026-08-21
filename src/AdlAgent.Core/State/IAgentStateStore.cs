using AdlAgent.Core.Api;

namespace AdlAgent.Core.State;

/// <summary>
/// Where the token and the configuration cache live between runs.
/// </summary>
/// <remarks>
/// An interface because tests need somewhere that is not a disk, not because
/// the platforms differ -- writing a file is the same everywhere. What
/// differs is <em>which directory</em>, and that is the host lifecycle
/// seam's business, not this one's.
/// </remarks>
public interface IAgentStateStore
{
    AgentState Load();

    void Save(AgentState state);

    /// <summary>The cached configuration, or <c>null</c> if none was ever written.</summary>
    CachedConfiguration? LoadConfig();

    void SaveConfig(SyncResponse config, DateTimeOffset fetchedAt);
}
