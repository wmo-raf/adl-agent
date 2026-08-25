namespace AdlAgent.Core.Status;

/// <summary>
/// Everything the local UI shows, in one answer.
/// </summary>
/// <remarks>
/// One snapshot rather than a handful of readable properties, because the
/// tray is drawing one picture: a status assembled from four values read a
/// moment apart can show a machine as paired and unconfigured at once, and
/// the technician would be right to believe it.
/// </remarks>
public sealed record AgentStatusSnapshot
{
    public required string AgentVersion { get; init; }

    /// <summary>The instance this machine sends to. One agent, one ADL.</summary>
    public required string AdlUrl { get; init; }

    // ---------- whether this machine has anywhere to send at all ----------
    //
    // Told apart from "configured but unreachable" on purpose. Both leave a
    // machine sending nothing, they look identical on the tray's Status tab,
    // and they are fixed by different people: one wants an administrator at
    // this machine, the other wants somebody who can see the network. Before
    // this, an unconfigured machine served AdlUrl as an empty string and the
    // window drew an empty row -- indistinguishable from a value the service
    // had not sent.

    /// <summary>True when this machine has an address it could send to.</summary>
    public bool Configured { get; init; } = true;

    /// <summary>
    /// Why not, when it is not: missing, unparseable, or plain HTTP to
    /// somewhere other than this machine. <c>null</c> when configured.
    /// </summary>
    public string? ConfigurationProblem { get; init; }

    /// <summary>
    /// What to do about it, in the terms of the tier this install is.
    /// <c>null</c> when configured.
    /// </summary>
    /// <remarks>
    /// Tier-shaped because the two tiers have genuinely different answers
    /// and only one of them is available to the person likely to be standing
    /// there -- see the README's known gaps.
    /// </remarks>
    public string? ConfigurationHint { get; init; }

    /// <summary>Unpaired, Paired, or RePairNeeded.</summary>
    public required string PairingState { get; init; }

    /// <summary>True when ADL has refused this machine's token.</summary>
    public required bool RePairNeeded { get; init; }

    public long? DeviceId { get; init; }

    public string? DeviceName { get; init; }

    public DateTimeOffset? PairedAt { get; init; }

    /// <summary>When ADL was last reached for configuration.</summary>
    public DateTimeOffset? LastSyncedAt { get; init; }

    /// <summary>True when the agent is working from the offline cache.</summary>
    public bool ConfigFromCache { get; init; }

    public long? ConfigVersion { get; init; }

    public int StationLinkCount { get; init; }

    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>ADL's word for this device at the last beat.</summary>
    public string? FleetStatus { get; init; }

    public int? ClockSkewSeconds { get; init; }

    public int CheckIntervalMinutes { get; init; }

    public int HeartbeatIntervalMinutes { get; init; }

    /// <summary>Why the last attempt to reach ADL failed, if it did.</summary>
    public string? LastError { get; init; }

    // ---------- what this machine is running, and what it could be ----------
    //
    // Here rather than left to the log because it answers the question a
    // technician standing at a machine actually has when HQ says "you are on
    // an old version": is this machine trying to update itself, and if it is
    // not, why not. The four states that answer it -- nothing published,
    // pinned, up to date, and could not fetch it -- want four different
    // people to do something, and only one of them is the technician.

    /// <summary>The last update check's outcome, by name.</summary>
    public string UpdateState { get; init; } = "";

    /// <summary>The version ADL last offered this machine, if any.</summary>
    public string? UpdateVersion { get; init; }

    /// <summary>True when an operator has pinned this machine to a version.</summary>
    public bool UpdatePinned { get; init; }

    /// <summary>One sentence about the last check, ADL's own where it had one.</summary>
    public string UpdateDetail { get; init; } = "";

    /// <summary>When the last update check ran, or <c>null</c> before the first.</summary>
    public DateTimeOffset? UpdateCheckedAt { get; init; }

    /// <summary>
    /// The last configuration sync somebody asked for at the machine, and what
    /// it came to.
    /// </summary>
    /// <remarks>
    /// Beside <see cref="LastSyncedAt"/> rather than folded into it. That one
    /// moves on every cycle whether anybody asked or not, so a window watching
    /// it to find out what a button did would report the next scheduled sync
    /// as the answer to a press that failed.
    /// </remarks>
    public Configuration.SyncAttempt? RequestedSync { get; init; }
}
