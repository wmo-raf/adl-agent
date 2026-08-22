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
}
