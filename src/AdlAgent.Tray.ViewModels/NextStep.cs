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
/// nothing at all for the technician the product is for: three tabs, two of
/// them empty until an administrator in another building acts, and nothing on
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
                Attention = TrayState.NeedsAttention,
                Text = "ADL has revoked this machine, and nothing is being sent until it is paired "
                    + "again. Ask your ADL administrator for a new pairing code, then paste it on "
                    + "the Pairing tab.",
                NoStations = "ADL has revoked this machine, so it can no longer read the stations "
                    + "linked to it.",
            };
        }

        if (status.PairingState != nameof(CorePairingState.Paired))
        {
            return new NextStep
            {
                Kind = NextStepKind.NotPaired,
                Attention = TrayState.NeedsAttention,
                Text = "Paste the pairing code your ADL administrator gave you, on the Pairing tab. "
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

        // Only the stations HQ has switched on. A disabled one is an
        // administrator's decision rather than anything a technician standing
        // at this machine can act on, and a line telling them to act on it
        // would be a line that never goes away.
        var live = stations.Where(station => station.Enabled).ToList();

        var unbound = live
            .Where(station => string.IsNullOrWhiteSpace(station.Config.LocalFolderPath))
            .ToList();

        if (unbound.Count > 0)
        {
            return new NextStep
            {
                Kind = NextStepKind.BindAFolder,
                Attention = TrayState.NeedsAttention,
                Text = string.Create(
                    CultureInfo.CurrentCulture,
                    $"Bind a folder to {unbound[0].StationName}: open the Stations tab, select it, "
                    + $"and say where its files are on this machine.{AlsoNeedOne(unbound.Count)}"),
            };
        }

        var failing = live.Where(station => !string.IsNullOrEmpty(station.Error)).ToList();

        if (failing.Count > 0)
        {
            return new NextStep
            {
                Kind = NextStepKind.FixAStation,
                Attention = TrayState.NeedsAttention,
                Text = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{failing[0].StationName} collected nothing last cycle: {failing[0].Error}"
                    + $"{AlsoWentQuiet(failing.Count)}"),
            };
        }

        return new NextStep
        {
            Kind = NextStepKind.NothingToDo,
            Attention = TrayState.Working,
            Text = live.Count == 0
                ? "Nothing to do — every station ADL linked to this machine is switched off in ADL."
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"Nothing to do — this machine is collecting for {Counted(live.Count)} and "
                    + $"sending to ADL."),
        };
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

    private static string AlsoWentQuiet(int total) => total switch
    {
        <= 1 => "",
        2 => " One other station collected nothing either.",
        _ => string.Create(
            CultureInfo.CurrentCulture, $" {total - 1} other stations collected nothing either."),
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
