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

    /// <summary>
    /// When each station link was last swept, or an empty log if none ever
    /// was. Unreadable counts as empty: the cost of forgetting is one extra
    /// sweep, and the cost of refusing to start is a machine somebody has to
    /// visit.
    /// </summary>
    SweepLog LoadSweeps();

    void SaveSweeps(SweepLog sweeps);

    /// <summary>
    /// Throw away everything this machine learned from the ADL instance it
    /// was talking to: the token, the cached configuration and the sweep log.
    /// </summary>
    /// <remarks>
    /// One method rather than three clears, because these three facts belong
    /// to one instance and are only ever wrong together. A machine repointed
    /// at a different ADL (<c>adl-agent set-url</c>) keeps none of them: the
    /// token was issued by the old instance, the cached configuration would
    /// have the tray listing the old instance's stations on a machine that is
    /// now unpaired, and the sweep log is keyed by station link id -- ids the
    /// new instance issues to entirely different stations, whose folders
    /// would then never get their first full sweep.
    /// <para>
    /// What is deliberately not cleared is <c>agent.ini</c>: where the
    /// machine reports is the one thing the repoint has just decided.
    /// </para>
    /// </remarks>
    void ForgetInstance();
}
