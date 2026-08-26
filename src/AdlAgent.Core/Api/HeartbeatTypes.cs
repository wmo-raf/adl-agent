namespace AdlAgent.Core.Api;

/// <summary>
/// One machine saying what it is and how it is doing.
/// </summary>
/// <remarks>
/// Every field is optional to ADL, which is the point: a heartbeat ADL
/// refuses is a heartbeat that never arrived, and the one fact the whole
/// liveness ladder rests on -- that this machine is alive -- is carried by
/// the request existing at all. So the agent sends what it managed to
/// gather and never lets a failed disk query cost it a beat.
/// </remarks>
public sealed record HeartbeatRequest
{
    public string AppVersion { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public long? UptimeSeconds { get; init; }

    /// <summary>
    /// This machine's own clock, as it reads at the moment the beat was
    /// built. ADL trusts it for nothing except the skew it computes from it
    /// -- which matters because the file windows this agent runs on are
    /// measured against exactly this clock.
    /// </summary>
    public DateTimeOffset? DeviceTime { get; init; }

    /// <summary>Files seen and not yet accepted by ADL.</summary>
    public int? BacklogCount { get; init; }

    public CycleReport? LastCycle { get; init; }

    public IReadOnlyList<VolumeReport> Disk { get; init; } = [];
}

/// <summary>The last scan cycle that ran to completion, and what it did.</summary>
public sealed record CycleReport
{
    public DateTimeOffset? CompletedAt { get; init; }
    public IReadOnlyList<CycleLinkReport> Links { get; init; } = [];
}

/// <summary>One station's share of that cycle.</summary>
public sealed record CycleLinkReport
{
    public long StationLinkId { get; init; }
    public int? Scanned { get; init; }
    public int? Offered { get; init; }
    public int? Uploaded { get; init; }
    public int? Failed { get; init; }

    /// <summary>What went wrong for this station, if anything did.</summary>
    public string? Error { get; init; }
}

/// <summary>Free space where this machine's watched folders live.</summary>
public sealed record VolumeReport
{
    public string Volume { get; init; } = "";
    public long? FreeBytes { get; init; }
    public long? TotalBytes { get; init; }
}

/// <summary>ADL's answer to a beat: the clock, the cadence, and the verdict.</summary>
public sealed record HeartbeatResponse
{
    public long DeviceId { get; init; }
    public DateTimeOffset? ServerTime { get; init; }

    /// <summary>
    /// How far this machine's clock is from ADL's, as ADL measured it. Sent
    /// back because the machine is the only party that can do anything about
    /// it.
    /// </summary>
    public int? ClockSkewSeconds { get; init; }

    /// <summary>ADL's own word for this device: online, degraded, offline.</summary>
    public string Status { get; init; } = "";

    public int HeartbeatIntervalMinutes { get; init; }
    public int CheckIntervalMinutes { get; init; }

    /// <summary>
    /// How often a station offers its whole folder rather than only what the
    /// candidate window admits. Zero or less switches sweeps off.
    /// </summary>
    /// <remarks>
    /// The same number <see cref="DeviceConfig.ReconciliationIntervalHours"/>
    /// carries, and nullable for the same reason: an ADL that predates the
    /// setting sends nothing, and a zero would be indistinguishable from that
    /// if this were an <see cref="int"/>.
    /// <para>
    /// <see cref="Cycle.ReconciliationSweep"/> reads the sync copy, not this
    /// one -- a cycle re-syncs before it decides anything, so the number it
    /// plans from is never older than the cycle itself. This one is here
    /// because the beat is where a deployment-wide setting is most visible:
    /// changing it moves no <c>config_version</c>, so there is nothing in a
    /// sync response to say the number is new, and a technician reading a
    /// beat should be able to see the cadence ADL currently believes in.
    /// </para>
    /// </remarks>
    public int? ReconciliationIntervalHours { get; init; }

    public long ConfigVersion { get; init; }
}
