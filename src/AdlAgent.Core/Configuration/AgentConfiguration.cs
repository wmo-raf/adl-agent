using AdlAgent.Core.Api;

namespace AdlAgent.Core.Configuration;

/// <summary>
/// The configuration the agent is working from, and where it came from.
/// </summary>
/// <remarks>
/// <see cref="FromCache"/> travels with the configuration rather than being
/// asked for separately, so that anything acting on it -- the cycle, the
/// tray, the heartbeat -- is holding the provenance in the same hand as the
/// values. "These folders, as of a sync eleven hours ago" is a different
/// fact from "these folders", and only one of them explains why a station
/// ADL has since disabled is still being scanned.
/// </remarks>
public sealed record AgentConfiguration
{
    public required SyncResponse Sync { get; init; }

    /// <summary>When ADL served this, not when it was read from disk.</summary>
    public required DateTimeOffset FetchedAt { get; init; }

    /// <summary>True when this came off the disk because ADL was unreachable.</summary>
    public required bool FromCache { get; init; }

    public long Version => Sync.ConfigVersion;

    /// <summary>Every station link on this device, across all its connections.</summary>
    public IEnumerable<StationLinkConfig> StationLinks =>
        Sync.Connections.SelectMany(connection => connection.StationLinks);
}
