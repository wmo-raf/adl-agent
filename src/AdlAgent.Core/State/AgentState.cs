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

/// <summary>
/// When each station link was last offered its whole folder.
/// </summary>
/// <remarks>
/// The one thing about its own work the agent remembers between runs, and it
/// is deliberately not a record of what was delivered -- that stays ADL's, so
/// that a lost file here costs a sweep and never a gap. It is kept because
/// the alternative is worse: a service restarts on every crash, every reboot
/// and every auto-update, and a machine that swept on every start would offer
/// its entire folder -- two hundred manifest pages on the folders this
/// product exists for -- down a country link that is slow on its good days.
/// </remarks>
public sealed record SweepLog
{
    /// <summary>Station link id to the last time its whole folder was offered.</summary>
    public IReadOnlyDictionary<long, DateTimeOffset> Swept { get; init; } =
        new Dictionary<long, DateTimeOffset>();
}
