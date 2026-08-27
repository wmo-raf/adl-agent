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

    /// <summary>
    /// The last cycle, as one rolling picture of every station.
    /// </summary>
    /// <remarks>
    /// Kept beside <see cref="CompletedPasses"/> indefinitely, and this is
    /// not a transition measure. Agents update themselves through the release
    /// feed; ADL instances are upgraded by a person, one country at a time --
    /// so a new agent talking to a plugin that predates
    /// <see cref="CompletedPasses"/> is the normal, long-lived state across
    /// twenty-six ministries. It is also what
    /// <c>AgentDevice.last_cycle_completed_at</c> is written from, which
    /// drives ADL's cycle-stuck check: an agent that stopped sending this
    /// would make every auto-updated machine report as stuck to every ADL not
    /// yet upgraded -- a fleet-wide false alarm nobody in those countries
    /// caused.
    /// <para>
    /// The cost is one small duplicated object per beat. A plugin that
    /// understands passes prefers them and ignores this.
    /// </para>
    /// </remarks>
    public CycleReport? LastCycle { get; init; }

    /// <summary>
    /// The unit passes that finished since the last beat ADL accepted.
    /// </summary>
    /// <remarks>
    /// The history <see cref="LastCycle"/> cannot be: a beat overwrites that,
    /// so ADL has never held more than one cycle's worth of what a machine
    /// did. These are the passes themselves, each one whole, and ADL stores a
    /// row per station per pass.
    /// <para>
    /// Cut-short passes are here too, carrying <c>Completed = false</c> and
    /// the sentence that says why. The pass whose absence is hardest to
    /// explain is the one that went wrong.
    /// </para>
    /// <para>
    /// On the beat rather than an endpoint of their own: the beat is already
    /// authenticated, already throttled, and already designed to be lossy
    /// without consequence. At a five-minute beat against a ten-minute cycle
    /// each one carries about a cycle's worth and the call count does not
    /// move.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CyclePassReport> CompletedPasses { get; init; } = [];

    /// <summary>
    /// Passes this machine made and could not keep, because ADL was
    /// unreachable for longer than the queue is deep.
    /// </summary>
    /// <remarks>
    /// Sent so that the shedding is recorded rather than silent: a gap in
    /// ADL's history that nothing accounts for is a gap somebody will read as
    /// a machine that stopped. Nothing is actually lost -- the cycle log on
    /// the machine keeps every pass regardless -- and this is what says where
    /// to go and look.
    /// </remarks>
    public int? DroppedPasses { get; init; }

    public IReadOnlyList<VolumeReport> Disk { get; init; } = [];
}

/// <summary>
/// One unit pass, whole: what it walked, what each of its stations did, and
/// a few of the files that did not arrive.
/// </summary>
/// <remarks>
/// The wire form of <see cref="Diagnostics.CycleRecord"/>, and deliberately
/// not that record itself. The record carries a bounded account of every file
/// the pass touched and runs to kilobytes; this is the part ADL stores, which
/// is a row per station plus a handful of names.
/// </remarks>
public sealed record CyclePassReport
{
    /// <summary>When the pass started.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>How long it took, in seconds.</summary>
    public double? Seconds { get; init; }

    /// <summary>The folder this unit is named by: the first one it walked.</summary>
    public string Unit { get; init; } = "";

    /// <summary>What started it: see <see cref="Diagnostics.CycleTriggers"/>.</summary>
    public string Trigger { get; init; } = "";

    /// <summary>False when the pass was cut short, and then <see cref="Stopped"/> says why.</summary>
    public bool Completed { get; init; }

    /// <summary>Why the pass did not finish, when it did not.</summary>
    public string? Stopped { get; init; }

    /// <summary>
    /// How many folders the pass actually walked.
    /// </summary>
    /// <remarks>
    /// A count and not the list. For a station filed by date the list is the
    /// dated sub-folders the cycle expanded to, which is worth having on the
    /// machine and is not worth a column per pass in a country's database;
    /// the number still separates "it looked in one folder" from "it walked a
    /// year of them".
    /// </remarks>
    public int? Folders { get; init; }

    public IReadOnlyList<CyclePassStation> Stations { get; init; } = [];

    /// <summary>A few of the files this pass saw and did not deliver.</summary>
    public IReadOnlyList<CyclePassFile> Missing { get; init; } = [];
}

/// <summary>One station's share of one pass.</summary>
public sealed record CyclePassStation
{
    public long StationLinkId { get; init; }

    /// <summary>Files in the folder matching this station's pattern.</summary>
    public int? Scanned { get; init; }

    /// <summary>Files left alone because they were still being written.</summary>
    public int? Held { get; init; }

    /// <summary>Files put in front of ADL.</summary>
    public int? Offered { get; init; }

    /// <summary>Files ADL asked for.</summary>
    public int? Wanted { get; init; }

    /// <summary>Files ADL took.</summary>
    public int? Uploaded { get; init; }

    /// <summary>Files that did not go.</summary>
    public int? Failed { get; init; }

    /// <summary>What this machine has that ADL does not.</summary>
    public int? Backlog { get; init; }

    /// <summary>What went wrong for this station, if anything did.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// One file the pass saw and ADL did not receive, and why.
/// </summary>
/// <remarks>
/// The point of the whole field. ADL already stores the name of every file it
/// received; what it has never had is the names of the ones that were seen
/// and did not arrive. That negative space is where "the vendor renamed its
/// files on the fourteenth" lives, and it is the difference between "this
/// station is quiet" and "this station is quiet because the files are now
/// called something else".
/// </remarks>
public sealed record CyclePassFile
{
    public string Name { get; init; } = "";

    /// <summary>Held, failed or unmatched: see <see cref="Diagnostics.FileOutcomes"/>.</summary>
    public string Outcome { get; init; } = "";

    /// <summary>The sentence that says why, when there is one.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The station it belongs to, or null when it belongs to none -- which is
    /// what an unmatched file is.
    /// </summary>
    public long? StationLinkId { get; init; }
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
