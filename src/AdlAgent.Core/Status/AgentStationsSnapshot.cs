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
    /// <summary>The connections, in the order ADL sent them.</summary>
    /// <remarks>
    /// Beside the stations rather than around them, and that is deliberate.
    /// Both lists are built from one walk of the configuration, so they cannot
    /// disagree; keeping them parallel leaves the flat list intact for the
    /// questions that are about the whole machine -- has ADL linked anything,
    /// has anything got a folder bound -- which is every question
    /// <c>NextSteps.For</c> asks. Nesting would make those re-flatten it at
    /// the call site, which is this class's flattening moved somewhere worse.
    /// <para>
    /// Carried at all because two facts about a connection are not recoverable
    /// from its stations. A connection ADL has switched off reaches
    /// <see cref="AgentStationSnapshot.Enabled"/> only as a false on every one
    /// of its stations, indistinguishable from somebody having switched each
    /// station off individually; and a connection with no station links at all
    /// leaves no trace in a flat list, so an administrator who has made one and
    /// not yet linked to it would look, from the machine, exactly like an
    /// administrator who has done nothing.
    /// </para>
    /// </remarks>
    public IReadOnlyList<AgentConnectionSnapshot> Connections { get; init; } = [];

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

    /// <summary>
    /// The instant every station here was judged against.
    /// </summary>
    /// <remarks>
    /// On this record rather than on each station, and that is load-bearing
    /// twice over. The tray decides whether to rebuild its rows by comparing
    /// the station list it was handed with the last one; an instant that moved
    /// every few seconds would sit inside that comparison and rebuild forty
    /// rows on every poll, taking the selection out from under whoever was
    /// about to press Edit settings. Out here it moves freely, and the ages a
    /// row writes are measured from the same clock its dot was decided by --
    /// rather than from whatever the window happened to read separately.
    /// </remarks>
    public DateTimeOffset AsOf { get; init; }
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

    /// <summary>
    /// When ADL last received anything for this station. Null means never.
    /// </summary>
    /// <remarks>
    /// ADL's record rather than this machine's memory of what it sent, which
    /// is what makes it worth carrying: the cycle report beside it is held in
    /// memory and is empty after a restart, while this is as true on a
    /// machine that came up a minute ago as on one that has been running for
    /// a month.
    /// </remarks>
    public DateTimeOffset? LastReceivedAt { get; init; }

    /// <summary>
    /// Whether data is actually reaching ADL for this station.
    /// </summary>
    /// <remarks>
    /// Decided here rather than by whoever draws the row, for two reasons
    /// that both matter. It is the one fact on this record that depends on
    /// the clock as well as on the data, so a UI that computed it would go
    /// on showing the answer it computed when the row was built -- and the
    /// tray only rebuilds rows when this snapshot changes, which on a settled
    /// machine is never. Carried here, the snapshot itself changes when a
    /// station crosses its window, and every reader notices.
    /// </remarks>
    public StationFlow Flow { get; init; }

    /// <summary>HQ's collection start date. Shown, never edited from here.</summary>
    public DateTimeOffset? StartDate { get; init; }

    public string Timezone { get; init; } = "";

    /// <summary>The tier this machine may write.</summary>
    public required StationLinkAppConfig Config { get; init; }

    /// <summary>Files in the folder matching this station's pattern last cycle.</summary>
    /// <summary>True while this station is being collected at this moment.</summary>
    /// <remarks>
    /// What stops the row showing a stale count as though it were the news. A
    /// station part-way through a backlog has last pass's numbers against it
    /// and is busy replacing them, and a grid that showed only the old ones
    /// reads as a station nothing is happening to.
    /// </remarks>
    public bool Collecting { get; init; }

    public int? Scanned { get; init; }

    public int? Offered { get; init; }

    public int? Uploaded { get; init; }

    public int? Failed { get; init; }

    /// <summary>What went wrong for this station last cycle, if anything did.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// What a collect somebody asked for at the machine came to, when that is
    /// more recent than the last cycle.
    /// </summary>
    /// <remarks>
    /// Beside the cycle's counts rather than replacing them, and null the
    /// moment a scheduled cycle overtakes it. The two are answers to different
    /// questions -- "what is this machine doing" and "what did the thing I
    /// just pressed do" -- and a row that showed a requested collect for ever
    /// would go on reporting a number from last Tuesday while a cycle five
    /// minutes ago said something else.
    /// </remarks>
    public Cycle.RequestedCollect? Requested { get; init; }
}

/// <summary>
/// One connection: what ADL calls it, and whether ADL is running it.
/// </summary>
/// <remarks>
/// Everything here is HQ's tier and none of it is writable from the machine
/// -- <c>AgentConnection</c> has no app-editable fields at all, unlike the
/// station link beneath it -- so this is a thing to read and to group by, and
/// never a thing to act on.
/// </remarks>
public sealed record AgentConnectionSnapshot
{
    public required long ConnectionId { get; init; }

    public string ConnectionName { get; init; } = "";

    /// <summary>The ADL network this connection collects for.</summary>
    public string Network { get; init; } = "";

    /// <summary>
    /// False when HQ has switched off the whole connection.
    /// </summary>
    /// <remarks>
    /// The flag itself, unmixed with its stations'. It is the one fact that
    /// tells a technician the folders on this machine are fine and there is
    /// nothing here to fix.
    /// </remarks>
    public bool Enabled { get; init; }

    /// <summary>
    /// How long one of this vendor's stations may say nothing before it is
    /// called quiet.
    /// </summary>
    /// <remarks>
    /// Carried on the connection because that is where ADL states it, and
    /// resolved onto each of its stations by
    /// <see cref="AgentStationsReader"/> -- so that the questions asked of
    /// the whole machine, which span connections that may disagree, stay
    /// questions about a flat list.
    /// </remarks>
    public int? StaleAfterMinutes { get; init; }
}
