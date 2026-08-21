using AdlAgent.Core.Api;

namespace AdlAgent.Core.State;

/// <summary>
/// The little that survives a restart: who this machine is to ADL.
/// </summary>
public sealed record AgentState
{
    /// <summary>The device token, or <c>null</c> before pairing.</summary>
    public string? Token { get; init; }

    public DeviceSummary? Device { get; init; }

    /// <summary>
    /// Set when ADL last refused the token. Persisted rather than held in
    /// memory so that restarting the service -- the first thing anyone tries
    /// -- does not hide a revocation until the next call.
    /// </summary>
    public bool RePairNeeded { get; init; }

    public DateTimeOffset? PairedAt { get; init; }
}

/// <summary>The last configuration ADL served, and when it served it.</summary>
public sealed record CachedConfiguration
{
    public DateTimeOffset FetchedAt { get; init; }

    public SyncResponse Config { get; init; } = new();
}
