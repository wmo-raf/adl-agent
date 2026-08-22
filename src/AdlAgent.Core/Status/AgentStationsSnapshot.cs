using AdlAgent.Core.Api;

namespace AdlAgent.Core.Status;

/// <summary>
/// Every station this machine sends for, as the tray lists them.
/// </summary>
/// <remarks>
/// One answer rather than a station list the tray then has to date, for the
/// same reason <see cref="AgentStatusSnapshot"/> is one answer: the list and
/// its provenance are read together or the technician is shown a folder
/// binding without being told it came off the disk during an outage.
/// </remarks>
public sealed record AgentStationsSnapshot
{
    /// <summary>The stations, in the order ADL sent them.</summary>
    public required IReadOnlyList<AgentStationSnapshot> Stations { get; init; }

    /// <summary>When ADL was last actually reached. Null on a machine that never has.</summary>
    public DateTimeOffset? LastSyncedAt { get; init; }

    /// <summary>True when this list came off the disk because ADL was unreachable.</summary>
    public bool ConfigFromCache { get; init; }

    public long? ConfigVersion { get; init; }

    /// <summary>
    /// When the cycle these counts come from finished, or null if none has
    /// since the service started.
    /// </summary>
    public DateTimeOffset? LastCycleAt { get; init; }
}

/// <summary>
/// One station: what ADL says it is, where this machine looks for it, and
/// how that went last time.
/// </summary>
/// <remarks>
/// The two tiers are kept apart on purpose (decision #260).
/// <see cref="Config"/> is the technician's -- the box the tray lets them
/// type in -- and everything beside it is HQ's, carried so the tray can show
/// it greyed out rather than pretend the station has no start date.
/// </remarks>
public sealed record AgentStationSnapshot
{
    public required long StationLinkId { get; init; }

    public long ConnectionId { get; init; }

    public string ConnectionName { get; init; } = "";

    public string StationName { get; init; } = "";

    /// <summary>The station's identifier in ADL, which is what a vendor's filenames usually carry.</summary>
    public string StationId { get; init; } = "";

    public string? WigosId { get; init; }

    /// <summary>
    /// False when HQ has switched off this station or its whole connection.
    /// </summary>
    /// <remarks>
    /// One flag for both, because the technician can do nothing about either
    /// and the difference is HQ's business. What matters at the machine is
    /// that this station is not being scanned and that is not a fault here.
    /// </remarks>
    public bool Enabled { get; init; }

    /// <summary>The oldest file ADL still wants for this station.</summary>
    public DateTimeOffset? Watermark { get; init; }

    /// <summary>HQ's collection start date. Shown, never edited from here.</summary>
    public DateTimeOffset? StartDate { get; init; }

    public string Timezone { get; init; } = "";

    /// <summary>The tier this machine may write.</summary>
    public required StationLinkAppConfig Config { get; init; }

    /// <summary>Files in the folder matching this station's pattern last cycle.</summary>
    public int? Scanned { get; init; }

    public int? Offered { get; init; }

    public int? Uploaded { get; init; }

    public int? Failed { get; init; }

    /// <summary>What went wrong for this station last cycle, if anything did.</summary>
    public string? Error { get; init; }
}
