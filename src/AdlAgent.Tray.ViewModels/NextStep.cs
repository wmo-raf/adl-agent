using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AdlAgent.Core.Status;
using CorePairingState = AdlAgent.Core.Pairing.PairingState;

namespace AdlAgent.Tray;

/// <summary>
/// The one line at the top of the window: what to do now, and who has to do
/// it.
/// </summary>
/// <remarks>
/// The tray used to say only what the machine <em>is</em>. That is enough for
/// somebody who already knows how ADL and this agent fit together, and it is
/// nothing at all for the technician the product is for: tabs that stay
/// empty until an administrator in another building acts, and nothing on
/// screen saying which of them matters now.
/// <para>
/// This is deliberately not a wizard. The moments that need guidance are not
/// only on day one -- a station linked six months later lands in exactly the
/// same unbound state as the first one, and a wizard that ran once is not
/// there for it. A line that is always right guides both, and it does not put
/// a second copy of the folder-binding screen beside the first to drift away
/// from it.
/// </para>
/// <para>
/// <see cref="Kind"/> is the decision and the two sentences are renderings of
/// it, written together here so the line and the empty station list cannot
/// come to disagree about which state the machine is in.
/// </para>
/// </remarks>
public sealed record NextStep
{
    /// <summary>Which state this is, for whoever has to switch on it.</summary>
    public required NextStepKind Kind { get; init; }

    /// <summary>The line itself, shown on every tab.</summary>
    public required string Text { get; init; }

    /// <summary>The colour the tray icon takes while this line stands.</summary>
    public required TrayState Attention { get; init; }

    /// <summary>
    /// What an empty station list says, when this state is one that leaves it
    /// empty.
    /// </summary>
    /// <remarks>
    /// "ADL has linked nothing to this device yet", "ADL is not answering" and
    /// "the service is not running" are three different problems for three
    /// different people, and until this they were one empty grid.
    /// </remarks>
    public string NoStations { get; init; } = "";

    /// <summary>
    /// True when the list on screen cannot be trusted to be what ADL holds.
    /// </summary>
    /// <remarks>
    /// The service is not running, this machine is not paired, or ADL is not
    /// answering and the configuration came off the disk. In every one of
    /// those the connections drawn are a memory rather than a fact, and the
    /// window has to say so across the whole tab -- because the alternative
    /// is a cached connection with no stations under it explaining, in the
    /// calmest possible terms, that an administrator has not linked anything
    /// yet. That sentence is true of the last sync and useless during an
    /// outage: it sends a technician to find an administrator when what is
    /// broken is the network.
    /// <para>
    /// Distinct from <see cref="NoStations"/> being non-empty, which is also
    /// true of <see cref="NextStepKind.NoStationsLinkedYet"/> -- and that one
    /// is a current, trustworthy answer. There ADL is answering and really
    /// has linked nothing, so each connection is left to say so for itself.
    /// </para>
    /// </remarks>
    public bool ListIsStale { get; init; }
}

/// <summary>
/// The states the window has something different to say about, in the order
/// they are decided.
/// </summary>
public enum NextStepKind
{
    /// <summary>Nothing has been heard from the service yet.</summary>
    Unknown,

    /// <summary>The service is not running, so nothing is being collected.</summary>
    ServiceNotRunning,

    /// <summary>This machine has not been told where its ADL is.</summary>
    NotConfigured,

    /// <summary>ADL has revoked this machine's token.</summary>
    RePairNeeded,

    /// <summary>No pairing code has been redeemed on this machine.</summary>
    NotPaired,

    /// <summary>Paired moments ago; ADL has not answered yet.</summary>
    WaitingForFirstSync,

    /// <summary>ADL cannot be reached, so the agent is working from its cache.</summary>
    AdlNotAnswering,

    /// <summary>ADL is answering and has linked no stations to this device.</summary>
    NoStationsLinkedYet,

    /// <summary>A station has no local folder, so nothing can be found for it.</summary>
    BindAFolder,

    /// <summary>A station collected nothing last cycle and said why.</summary>
    FixAStation,

    /// <summary>
    /// A station is configured, blaming nothing, and ADL has had nothing from
    /// it for longer than its vendor's window.
    /// </summary>
    StationWentQuiet,

    /// <summary>Everything a person could do has been done.</summary>
    NothingToDo,
}

/// <summary>Works out which <see cref="NextStep"/> a machine is on.</summary>
/// <remarks>
/// A free function over three facts -- whether the service answered, what it
/// said, and the stations ADL linked -- and nothing else. It reads the
/// stations as they arrived rather than the rows a technician may be typing
/// into: the line is about what ADL holds, and an unsaved folder path in a box
/// has not bound anything yet.
/// </remarks>
public static class NextSteps
{
    /// <summary>Before the first answer from the service.</summary>
    public static readonly NextStep Unknown = new()
    {
        Kind = NextStepKind.Unknown,
        ListIsStale = true,
        Attention = TrayState.Unknown,
        Text = "Checking what this machine is doing…",
        NoStations = "Checking what this machine is doing…",
    };

    /// <param name="serviceReached">False when the last poll got no answer at all.</param>
    /// <param name="status">The last answer, or null before there has been one.</param>
    /// <param name="stations">The stations ADL has linked to this device.</param>
    public static NextStep For(
        bool serviceReached,
        AgentStatusSnapshot? status,
        IReadOnlyList<AgentStationSnapshot> stations)
    {
        // Before everything: a window that cannot reach the service knows
        // nothing else, and every line below would be describing a machine it
        // has not heard from.
        if (!serviceReached)
        {
            return new NextStep
            {
                Kind = NextStepKind.ServiceNotRunning,
                ListIsStale = true,
                Attention = TrayState.Stopped,
                Text = "The ADL Agent service is not running on this machine, so nothing is being "
                    + "collected or sent. An administrator starts it again from Services.",
                NoStations = "The ADL Agent service is not running on this machine, so this window "
                    + "cannot ask it which stations are linked to this device.",
            };
        }

        if (status is null)
        {
            return Unknown;
        }

        // Then: a machine with no address cannot be paired, cannot sync and
        // cannot be revoked, so every line below would be describing a state
        // it is not in.
        if (!status.Configured)
        {
            return new NextStep
            {
                Kind = NextStepKind.NotConfigured,
                ListIsStale = true,
                Attention = TrayState.NeedsAttention,
                Text = "This machine has not been told where its ADL is, so it is sending nothing. "
                    + status.ConfigurationHint,
                NoStations = "This machine has no ADL address, so ADL has never told it about any "
                    + "stations.",
            };
        }

        if (status.RePairNeeded)
        {
            return new NextStep
            {
                Kind = NextStepKind.RePairNeeded,
                ListIsStale = true,
                Attention = TrayState.NeedsAttention,
                Text = "ADL has revoked this machine, and nothing is being sent until it is paired "
                    + "again. Ask your ADL administrator for a new pairing code, then paste it on "
                    + "the Status tab.",
                NoStations = "ADL has revoked this machine, so it can no longer read the stations "
                    + "linked to it.",
            };
        }

        if (status.PairingState != nameof(CorePairingState.Paired))
        {
            return new NextStep
            {
                Kind = NextStepKind.NotPaired,
                ListIsStale = true,
                Attention = TrayState.NeedsAttention,
                Text = "Paste the pairing code your ADL administrator gave you, on the Status tab. "
                    + "Nothing else about ADL needs setting up on this machine.",
                NoStations = "This machine is not paired with ADL yet, so ADL has not told it about "
                    + "any stations.",
            };
        }

        // Paired, and ADL has never answered. Told apart from the outage
        // below because it is not one: this is the second or two after a
        // pairing code was accepted, and the only right thing to do about it
        // is nothing.
        if (status.LastSyncedAt is null && status.LastError is null)
        {
            return new NextStep
            {
                Kind = NextStepKind.WaitingForFirstSync,
                ListIsStale = true,
                Attention = TrayState.NeedsAttention,
                Text = "Paired. Waiting for this device's configuration from ADL — this window "
                    + "updates on its own.",
                NoStations = "This machine has just paired and has not read its configuration from "
                    + "ADL yet. This window updates on its own.",
            };
        }

        // Before anything about the stations: an unreachable ADL is why the
        // list may be short or empty, and it is also why binding a folder
        // would not stick -- the tray writes settings through ADL and never
        // to the machine.
        if (status.ConfigFromCache || status.LastSyncedAt is null)
        {
            return new NextStep
            {
                Kind = NextStepKind.AdlNotAnswering,
                ListIsStale = true,
                Attention = TrayState.NeedsAttention,
                Text = Sentence(
                    "ADL is not answering, so nothing is being sent and no setting can be saved. "
                    + "Nothing on this machine needs changing: the files are kept and offered "
                    + "again when the link returns.",
                    status.LastError),
                NoStations = Sentence(
                    "ADL is not answering, so this window cannot say which stations are linked to "
                    + "this device.",
                    status.LastError),
            };
        }

        if (stations.Count == 0)
        {
            return new NextStep
            {
                Kind = NextStepKind.NoStationsLinkedYet,
                Attention = TrayState.NeedsAttention,
                Text = "Waiting for your ADL administrator to link stations to this device — this "
                    + "window updates on its own.",
                NoStations = "ADL has not linked any stations to this device yet. Your ADL "
                    + "administrator does that in the ADL admin; this window updates on its own, "
                    + "so there is nothing to press here.",
            };
        }

        // From here down the question is only about the stations, and it is
        // the same question each connection's row in the list asks about its
        // own -- so it is asked once, in one place, and rendered twice.
        var standing = StationStanding.Of(stations);

        if (standing.Kind == StandingKind.BindAFolder)
        {
            var first = standing.Unbound[0];

            return new NextStep
            {
                Kind = NextStepKind.BindAFolder,
                Attention = standing.Attention,
                Text = string.Create(
                    CultureInfo.CurrentCulture,
                    $"Bind a folder to {Named(first)}: open the Stations tab, select it, "
                    + $"and say where its files are on this machine.{AlsoNeedOne(standing.Unbound.Count)}"),
            };
        }

        if (standing.Kind == StandingKind.FixAStation)
        {
            var first = standing.Failing[0];

            return new NextStep
            {
                Kind = NextStepKind.FixAStation,
                Attention = standing.Attention,
                Text = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{Subject(first)} collected nothing last cycle: {first.Error}"
                    + $"{AlsoReportedOne(standing.Failing.Count)}"),
            };
        }

        // An instruction rather than a statement, which is what this class
        // owes an amber icon in the corner of the screen. Nobody standing at
        // this machine can know from here whether the logger died, the share
        // unmounted, or the vendor changed what it writes -- but all three are
        // answered by looking, and looking is a thing that can be told to
        // somebody.
        if (standing.Kind == StandingKind.Quiet)
        {
            var first = standing.Quiet[0];

            return new NextStep
            {
                Kind = NextStepKind.StationWentQuiet,
                Attention = standing.Attention,
                Text = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{Subject(first)} {Silence(first)} Open the Stations tab, select it, and "
                    + $"check status — the folder may be empty, or its pattern may no longer "
                    + $"match what the vendor is writing.{AlsoSilent(standing.Quiet.Count)}"),
            };
        }

        return new NextStep
        {
            Kind = NextStepKind.NothingToDo,
            Attention = standing.Attention,
            Text = standing.Kind == StandingKind.AllSwitchedOff
                ? "Nothing to do — every station ADL linked to this machine is switched off in ADL."
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"Nothing to do — this machine is collecting for {Counted(standing.Live.Count)} and "
                    + $"sending to ADL."),
        };
    }

    /// <summary>
    /// A station, and the connection it is under.
    /// </summary>
    /// <remarks>
    /// The connection is named because the station list is no longer one
    /// list. "Open the Stations tab, select it" was a complete instruction
    /// while every station was in one grid; with the stations split by
    /// connection it stops being one, and a technician on a machine serving
    /// two vendors has to guess which side of the split to look on. The name
    /// costs nothing to carry -- it is already on the station -- and it turns
    /// the line back into something that can be followed without knowing the
    /// answer first.
    /// <para>
    /// Omitted when there is no name to give, rather than rendering an empty
    /// clause: an older ADL that sends no connection name should produce the
    /// sentence this replaced, not a sentence with a hole in it.
    /// </para>
    /// </remarks>
    private static string Clause(AgentStationSnapshot station) =>
        string.IsNullOrWhiteSpace(station.ConnectionName)
            ? ""
            : $", under {station.ConnectionName}";

    /// <summary>"Kakamega, under Vaisala AWS" — for a phrase a colon follows.</summary>
    private static string Named(AgentStationSnapshot station) =>
        station.StationName + Clause(station);

    /// <summary>
    /// "Kakamega, under Vaisala AWS," — for a phrase a verb follows.
    /// </summary>
    /// <remarks>
    /// A second rendering rather than a second comma bolted on at the call
    /// site, because the comma is only right when there is a clause to close.
    /// On an ADL that sends no connection name this has to read "Kakamega
    /// collected nothing", not "Kakamega, collected nothing".
    /// </remarks>
    private static string Subject(AgentStationSnapshot station)
    {
        var clause = Clause(station);

        return clause.Length == 0 ? station.StationName : $"{station.StationName}{clause},";
    }

    /// <summary>
    /// How many other stations want the same thing doing, when more than one
    /// does.
    /// </summary>
    /// <remarks>
    /// One is named and the rest are counted. A line listing forty station
    /// names is a line nobody reads, and a line naming none of them is one
    /// nobody can act on -- so it names where to start and says how much is
    /// left after it. A technician who does the named one sees the line move
    /// to the next on the following poll, which is the whole of what a wizard
    /// would have given them.
    /// </remarks>
    private static string AlsoNeedOne(int total) => total switch
    {
        <= 1 => "",
        2 => " One other station needs one too.",
        _ => string.Create(CultureInfo.CurrentCulture, $" {total - 1} other stations need one too."),
    };

    /// <summary>
    /// How long this station has been silent, or that it never started.
    /// </summary>
    /// <remarks>
    /// The two are different instructions wearing one colour. A station that
    /// has sent and stopped is one whose folder used to work; a station that
    /// has never sent is one whose folder may never have been right, and
    /// telling somebody it "has sent nothing since" a date that does not
    /// exist would be the line inventing a history.
    /// </remarks>
    private static string Silence(AgentStationSnapshot station) =>
        station.LastReceivedAt is { } received
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"has sent nothing to ADL since {Display.Moment(received)}.")
            : "has never sent anything to ADL.";

    private static string AlsoReportedOne(int total) => total switch
    {
        <= 1 => "",
        2 => " One other station reported a problem too.",
        _ => string.Create(
            CultureInfo.CurrentCulture, $" {total - 1} other stations reported a problem too."),
    };

    private static string AlsoSilent(int total) => total switch
    {
        <= 1 => "",
        2 => " One other station has gone quiet too.",
        _ => string.Create(
            CultureInfo.CurrentCulture, $" {total - 1} other stations have gone quiet too."),
    };

    private static string Counted(int count) => count == 1
        ? "1 station"
        : string.Create(CultureInfo.CurrentCulture, $"{count} stations");

    /// <summary>
    /// The sentence, and what the agent last saw go wrong if it saw anything.
    /// </summary>
    /// <remarks>
    /// Appended rather than replacing it: "ADL is not answering" is what the
    /// person reading this can act on, and the transport's own words are what
    /// they read down the telephone to whoever can see the network.
    /// </remarks>
    private static string Sentence(string line, string? lastError) =>
        string.IsNullOrWhiteSpace(lastError) ? line : $"{line} ADL last said: {lastError}";
}
