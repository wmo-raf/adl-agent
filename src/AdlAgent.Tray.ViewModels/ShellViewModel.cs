using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AdlAgent.Core;
using AdlAgent.Core.Cycle;
using AdlAgent.Core.Diagnostics;
using AdlAgent.Core.Serialization;
using AdlAgent.Core.Status;
using AdlAgent.Windows.Platform;
using CorePairingState = AdlAgent.Core.Pairing.PairingState;

namespace AdlAgent.Tray;

/// <summary>
/// Everything the window shows, and the five things it can ask for.
/// </summary>
/// <remarks>
/// This holds no state of its own beyond what the last answer said. There is
/// no cache of the station list, no pending edit queue, no local copy of a
/// setting: every property here was last set from something the service
/// told it, and every button turns into a command the service implements.
/// The spec's "tray stays thin: reflects service state, holds no logic" is
/// enforced by there being nowhere else for a fact to come from.
/// <para>
/// Thin is not the same as empty. What is decided here is what to draw from
/// those answers -- which of them is the state the machine is in, what to
/// tell somebody to do about it, which fields differ from what ADL sent, and
/// when a poll may replace a row somebody is typing into -- and that is why
/// this class is in a <c>net10.0</c> assembly of its own rather than beside
/// the window. The window is <c>net10.0-windows</c>, which the test project
/// cannot reference; everything above holds a decision, and a decision
/// nothing can drive is a decision covered by reading.
/// </para>
/// <para>
/// It is also free of WPF, apart from the collection type the list binds to.
/// The window below it is layout.
/// </para>
/// </remarks>
public sealed class ShellViewModel : Observable
{
    private readonly AgentControlLink _agent;
    private readonly IAddressChange _adlAddress;

    private AgentStatusSnapshot? _status;
    private bool _serviceReached;
    private string _pairingCode = "";
    private string _message = "";
    private ConnectionViewModel? _selectedConnection;
    private StationViewModel? _selectedStation;
    private NextStep _nextStep = NextSteps.Unknown;
    private int _selectedTab = TrayTabs.Stations;

    /// <summary>
    /// The moment the header's "3 hours ago" is measured back from.
    /// </summary>
    /// <remarks>
    /// The service's own now, taken from the station list's
    /// <see cref="AgentStationsSnapshot.AsOf"/> on every refresh -- which is
    /// the same value the rows in the grid age against. One clock, because a
    /// header saying a heartbeat arrived three hours ago above a row saying a
    /// file arrived four hours ago is a comparison a technician makes, and
    /// two clocks would make it a false one.
    /// <para>
    /// This machine's own clock when the service has not answered, so that a
    /// header left showing the last thing it heard goes on aging it rather
    /// than freezing at whatever it said when the service went away.
    /// </para>
    /// </remarks>
    private DateTimeOffset _asOf = DateTimeOffset.UtcNow;

    /// <summary>
    /// The stations as ADL last sent them, whatever the rows below are
    /// showing.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Stations"/> because the rows are
    /// deliberately not rebuilt while somebody is typing into one, and the
    /// line at the top of the window is about what ADL holds rather than about
    /// what is in a box. A technician who has typed a folder path and not yet
    /// saved it has not bound anything, and the line should go on saying so.
    /// </remarks>
    private IReadOnlyList<AgentStationSnapshot> _linked = [];

    /// <summary>True once the tab has been picked from the machine's state.</summary>
    private bool _tabChosen;

    /// <summary>True once the connection has been picked from the machine's state.</summary>
    private bool _connectionChosen;

    /// <summary>
    /// The connections and stations the rows were last built from, as they
    /// arrived.
    /// </summary>
    /// <remarks>
    /// Both, in one string, and the connections are not optional. Comparing
    /// the stations alone -- which is what this did while the list was flat --
    /// is blind to a connection that has no station links: creating one, or
    /// switching one off, leaves the flat station list byte-identical, so the
    /// left-hand list would never learn the connection existed for as long as
    /// the tray ran.
    /// <para>
    /// One string rather than two comparisons because the two halves are
    /// built from a single walk of the configuration and always move
    /// together. Rebuilding them independently would let the list say
    /// "3 need a folder" beside a grid that has already been rebuilt without
    /// them.
    /// </para>
    /// </remarks>
    private string _shown = "";

    /// <summary>True while a modal station window is open over this one.</summary>
    /// <remarks>
    /// Either of them: the settings window a technician types into, or the
    /// read-only status window. Both hold a copy of a row, so both would be
    /// left editing or describing a station the list no longer contains if a
    /// poll rebuilt the rows underneath them.
    /// </remarks>
    private bool _editing;

    /// <summary>
    /// True while this class is assigning the selection itself, rather than a
    /// technician clicking.
    /// </summary>
    /// <remarks>
    /// The two are indistinguishable at the setter -- the list binds to it
    /// two-way and <see cref="Show"/> writes to it on every rebuild -- and
    /// telling them apart is the whole of what
    /// <see cref="ShowsConnectionHint"/> needs. A hint that said "click one to
    /// see its linked stations" and disappeared before the window was first
    /// drawn, because <see cref="Choose"/> had already picked a connection,
    /// would be a hint nobody ever read.
    /// </remarks>
    private bool _restoring;

    /// <summary>True once somebody has picked a connection for themselves.</summary>
    private bool _connectionClicked;

    /// <summary>
    /// True once somebody has asked for the code box back on a machine that
    /// is already paired.
    /// </summary>
    /// <remarks>
    /// Window state rather than machine state, and so here rather than in the
    /// service: the machine is paired and working either way, and what has
    /// changed is only that somebody standing at it has a code in their hand.
    /// <para>
    /// It exists because a rotation never revokes anything. ADL issues a
    /// fresh code with the old token deliberately left working -- so that a
    /// machine still shipping data does not stop between an administrator's
    /// click and a technician getting round to typing the code in -- which
    /// means a machine being rotated stays <c>Paired</c> and never asks for
    /// anything. Without this, whoever is holding that code has nowhere on
    /// screen to put it.
    /// </para>
    /// </remarks>
    private bool _pairAgain;

    /// <summary>
    /// The moment the sync this window asked for was started, while its answer
    /// is still owed.
    /// </summary>
    /// <remarks>
    /// The moment rather than a flag, because the status carries the last
    /// requested sync whoever asked for it -- another tray on another logon
    /// session, or this one before the service restarted -- and a window that
    /// reported the first finished attempt it saw would announce somebody
    /// else's press as the answer to its own.
    /// </remarks>
    private DateTimeOffset? _awaitedSync;

    /// <param name="adlAddress">
    /// How this window asks Windows to repoint the machine. Passed in by the
    /// tray's own composition root beside the control link, and by tests,
    /// which must not raise a consent prompt somebody has to click. The
    /// fallback is what a window built with one argument gets, and it is the
    /// real one on purpose: a default that quietly did nothing would be a
    /// button that quietly did nothing.
    /// </param>
    public ShellViewModel(AgentControlLink agent, IAddressChange? adlAddress = null)
    {
        _agent = agent;
        _adlAddress = adlAddress ?? new ElevatedAddressChange();

        PairCommand = new AsyncCommand(PairAsync, Failed, () => PairingCode.Trim().Length > 0);
        SyncCommand = new AsyncCommand(SyncAsync, Failed, () => _awaitedSync is null);
    }

    /// <summary>
    /// The connections, each holding its own stations.
    /// </summary>
    /// <remarks>
    /// What the window binds its left-hand list to. The stations are reached
    /// through the selected one rather than from a flat collection here,
    /// because a flat collection plus a filter would put the decision "which
    /// stations belong to this connection" either in this class as a
    /// repopulate-on-click (which destroys the rows) or in the window as a
    /// WPF collection view (which the test project cannot reference, this
    /// assembly being deliberately WPF-free).
    /// </remarks>
    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];

    public AsyncCommand PairCommand { get; }

    /// <summary>
    /// Ask ADL for this device's configuration now: Sync with ADL, above the
    /// connection list.
    /// </summary>
    /// <remarks>
    /// Grey while an answer is owed, which is also what stops somebody
    /// pressing it four times on a link slow enough to make them want to.
    /// </remarks>
    public AsyncCommand SyncCommand { get; }

    // ---------- what the header and the status tab draw ----------

    /// <summary>False when the service could not be reached at the last poll.</summary>
    public bool ServiceRunning => _serviceReached;

    /// <summary>
    /// Where this machine sends -- or, when it has not been told, that it has
    /// not been told.
    /// </summary>
    /// <remarks>
    /// An unconfigured machine used to serve this as an empty string and the
    /// window drew an empty row, which looks exactly like a value the service
    /// did not send. They are opposite problems: one wants somebody to
    /// configure this machine, the other wants somebody to find out why the
    /// service is not answering.
    /// </remarks>
    public string AdlUrl => _status is { Configured: false }
        ? "No ADL address is configured"
        : _status?.AdlUrl ?? "-";

    /// <summary>
    /// The same address as something the header can hand to a browser, or
    /// <c>null</c> when it is not one.
    /// </summary>
    /// <remarks>
    /// A <see cref="Uri"/> and not the string beside it, because the header
    /// binds this to a hyperlink and WPF would otherwise convert the string
    /// itself -- on every machine, including the ones where
    /// <see cref="AdlUrl"/> is a sentence rather than an address. Bindings are
    /// evaluated on collapsed elements too, so an unconfigured machine would
    /// fail that conversion once a poll for as long as the tray ran, into the
    /// binding log that exists to make a real mistake findable.
    /// <para>
    /// Only http and https. Anything else is not something to hand the shell
    /// on the strength of a row that says it is a web address, and a machine
    /// that somehow held one should show it as text rather than offer to open
    /// it.
    /// </para>
    /// </remarks>
    public Uri? AdlLink =>
        Uri.TryCreate(_status?.AdlUrl, UriKind.Absolute, out var link)
        && (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp)
            ? link
            : null;

    /// <summary>True when this machine has an address to send to.</summary>
    public bool IsConfigured => _status is null || _status.Configured;

    /// <summary>The tier-appropriate next step, when there is one to take.</summary>
    public string ConfigurationHint => _status?.ConfigurationHint ?? "";

    /// <summary>
    /// True when the window should be showing somebody how to give this
    /// machine an address.
    /// </summary>
    public bool NeedsConfiguring => !IsConfigured;

    /// <summary>
    /// True when the ADL row should offer to change the address.
    /// </summary>
    /// <remarks>
    /// From the moment the service has answered, and on every machine after
    /// that -- including the one with no address at all, which is the state
    /// the hint under this row is about, and including the machine whose
    /// technician has no administrator rights.
    /// <para>
    /// That last one is the point rather than an oversight. Hiding the button
    /// from whoever cannot use it would hide it from the person the window is
    /// for: a technician without rights is who these visits are made by, and
    /// the consent prompt is exactly where an administrator standing beside
    /// them types a password. What this window must not do is hide the button,
    /// or pretend the change succeeded.
    /// </para>
    /// <para>
    /// Nothing at all before the first answer, for the same reason
    /// <see cref="ShowsPairingBox"/> waits: there is no address to open the
    /// box on, and a dialog pre-filled with a dash is one that offers to point
    /// this machine at nothing.
    /// </para>
    /// </remarks>
    public bool ShowsChangeAdl => _status is not null;

    public string AgentVersion => _status?.AgentVersion ?? "-";

    public string DeviceName => _status?.DeviceName ?? "-";

    public string DeviceId => _status?.DeviceId?.ToString(CultureInfo.CurrentCulture) ?? "-";

    /// <summary>What this machine's pairing is, in words a technician reads.</summary>
    /// <remarks>
    /// A rendering, and named as one -- <see cref="AgentStatusSnapshot"/>
    /// carries a <c>PairingState</c> of its own, which is the raw state and
    /// is what <see cref="IsPaired"/> and <see cref="NeedsRePairing"/> below
    /// read. Two properties of one name holding different strings, three
    /// lines apart, is a mistake somebody makes exactly once and then cannot
    /// find.
    /// <para>
    /// The words themselves are the ones the rest of the window already uses:
    /// <see cref="Headline"/> says ADL has "revoked" this machine, and a row
    /// beneath it reading <c>RePairNeeded</c> would be the same fact in a
    /// vocabulary nobody outside this repository speaks.
    /// </para>
    /// </remarks>
    public string PairingLine
    {
        get
        {
            if (_status is null)
            {
                return "Unknown";
            }

            if (NeedsRePairing)
            {
                return "Revoked by ADL";
            }

            return IsPaired ? "Paired" : "Not paired yet";
        }
    }

    public bool IsPaired => _status?.PairingState == nameof(CorePairingState.Paired);

    public bool NeedsRePairing => _status?.RePairNeeded == true;

    /// <summary>True once ADL has ever admitted this machine.</summary>
    /// <remarks>
    /// The question every fact ADL supplies is gated on, and deliberately not
    /// <see cref="IsPaired"/>. That one is false during a revocation as well
    /// as before a first pairing, and the two want opposite things: a machine
    /// that has never paired has nothing to show, while a machine ADL revoked
    /// this morning wants its last heartbeat, its last sync and its last
    /// problem on screen more than at any other time in its life.
    /// <para>
    /// <c>PairedAt</c> survives a revocation -- <c>MarkRevoked</c> flips a
    /// flag and keeps the rest -- which is what makes it the right thing to
    /// ask.
    /// </para>
    /// </remarks>
    public bool HasEverPaired => _status?.PairedAt is not null;

    /// <summary>
    /// When this machine last paired, in the tense the line above it needs.
    /// </summary>
    /// <remarks>
    /// Two readings of one moment, because the state it sits under is in two
    /// different tenses. On a paired machine the moment is when what is on
    /// screen started being true; on a revoked one it is when it stopped, and
    /// "since" there would quietly claim the machine is still paired.
    /// </remarks>
    public string PairedSince
    {
        get
        {
            if (_status?.PairedAt is not { } paired)
            {
                return "";
            }

            var moment = Display.Moment(paired);

            return NeedsRePairing
                ? string.Create(CultureInfo.CurrentCulture, $"paired {moment} until then")
                : string.Create(CultureInfo.CurrentCulture, $"since {moment}");
        }
    }

    /// <summary>True when there is somewhere on screen to type a pairing code.</summary>
    /// <remarks>
    /// Three machines want one, and only two of them are asking. A machine
    /// that has never paired and a machine ADL has revoked both say so;
    /// a machine being rotated says nothing at all, because its old token is
    /// still working on purpose -- so the third is
    /// <see cref="_pairAgain"/>, which is somebody saying it for it.
    /// <para>
    /// Nothing at all until the service has answered. A code box drawn beside
    /// a line reading "Checking what this machine is doing" would be offering
    /// a remedy for a state nobody has established yet.
    /// </para>
    /// </remarks>
    public bool ShowsPairingBox =>
        _status is not null && (!IsPaired || NeedsRePairing || _pairAgain);

    /// <summary>True when a working machine should offer to pair again.</summary>
    /// <remarks>
    /// A line rather than a box, because on all but a handful of days in a
    /// machine's life there is no code to type -- and a code box standing
    /// open on a machine that must not use one is the screen this tab was
    /// folded in to delete.
    /// </remarks>
    public bool ShowsPairAgain => _status is not null && IsPaired && !_pairAgain;

    /// <summary>
    /// True when the box on screen was asked for rather than needed.
    /// </summary>
    /// <remarks>
    /// The way back out, and only where there is one to take. A machine that
    /// has never paired, or one ADL has revoked, has nothing to cancel to:
    /// the box is what that machine is for until a code goes into it, and a
    /// Cancel there would offer to hide the only thing on the page worth
    /// pressing.
    /// <para>
    /// It follows that this can only be true on a paired machine -- which is
    /// also the machine where leaving the box open costs something, because
    /// the tray's window hides rather than closes, so a box opened by mistake
    /// would still be standing there tomorrow.
    /// </para>
    /// </remarks>
    public bool ShowsCancelPairing => _pairAgain && IsPaired;

    /// <summary>Ask for the code box on a machine that is already paired.</summary>
    /// <remarks>
    /// Local, and so not a command: it goes nowhere near the service. What it
    /// undoes is <see cref="ShowsPairAgain"/> in the same movement, so the
    /// line and the box it opens are never both on screen.
    /// </remarks>
    public void PairAgain()
    {
        if (_pairAgain)
        {
            return;
        }

        _pairAgain = true;

        Message = "";

        Raise(nameof(ShowsPairingBox));
        Raise(nameof(ShowsPairAgain));
        Raise(nameof(ShowsCancelPairing));
    }

    /// <summary>Put the code box away again, unused.</summary>
    /// <remarks>
    /// The code goes with it. What is in that box is one specific credential
    /// somebody has decided not to redeem, and a half-typed one reappearing
    /// the next time a technician opens this window is at best confusing and
    /// at worst the wrong device's.
    /// </remarks>
    public void CancelPairAgain()
    {
        if (!_pairAgain)
        {
            return;
        }

        _pairAgain = false;

        PairingCode = "";
        Message = "";

        Raise(nameof(ShowsPairingBox));
        Raise(nameof(ShowsPairAgain));
        Raise(nameof(ShowsCancelPairing));
    }

    /// <summary>
    /// Say that this machine could not open a browser for the header's link.
    /// </summary>
    /// <remarks>
    /// Here rather than in the window, even though it is the window that
    /// asked Windows and the window that was refused. Every other sentence
    /// this program says to a technician is written in this class, and the
    /// one exception would be the one no test could reach.
    /// <para>
    /// The address is repeated into the sentence on purpose: a click that
    /// went nowhere leaves somebody needing to type it on another machine,
    /// and the row it came from is 11px grey three inches above.
    /// </para>
    /// </remarks>
    public void BrowserRefused() =>
        Message = string.Create(
            CultureInfo.CurrentCulture,
            $"This machine could not open a browser. The address is {AdlUrl}.");

    /// <summary>What ADL last made of this machine, in words.</summary>
    /// <remarks>
    /// ADL sends the state it stores -- <c>cycle_stuck</c> -- and has the
    /// words for it (<c>Liveness.LABELS</c> in the agent plugin) but keeps
    /// them on its own side of the wire, so this row and the header sentence
    /// above it were printing an identifier at a technician.
    /// <para>
    /// Rendered here rather than asked for, because an agent meets whichever
    /// plugin version its country is running: 26 instances do not upgrade
    /// together, and a phrase that only arrived from a new enough ADL would
    /// leave the old ones showing exactly what this fixes. If ADL ever does
    /// send the words, they win and this becomes the fallback.
    /// </para>
    /// <para>
    /// The consequence rather than the mechanism, and short enough to sit in
    /// a sentence. The pair worth separating is <c>offline</c> from
    /// <c>cycle_stuck</c>: both mean nothing is arriving, and the difference
    /// is whether somebody has to walk to the machine.
    /// </para>
    /// </remarks>
    public string FleetStatus
    {
        get
        {
            var state = _status?.FleetStatus;

            if (string.IsNullOrWhiteSpace(state))
            {
                return "-";
            }

            return FleetStates.TryGetValue(state, out var said) ? said : Humanised(state);
        }
    }

    /// <summary>
    /// A state this build has never heard of, made readable anyway.
    /// </summary>
    /// <remarks>
    /// ADL owns this vocabulary and can add to it, and an agent in the field
    /// is months behind whatever HQ deploys. The words will be wrong -- they
    /// are ADL's identifier with its underscores taken out -- but "Clock
    /// skewed" is a thing a technician can read down a telephone and
    /// <c>clock_skewed</c> is not.
    /// </remarks>
    private static string Humanised(string state)
    {
        var words = state.Replace('_', ' ').Trim();

        return words.Length == 0 ? "-" : char.ToUpperInvariant(words[0]) + words[1..];
    }

    public string LastHeartbeat => Display.Moment(_status?.LastHeartbeatAt);

    public string LastSynced => Display.Moment(_status?.LastSyncedAt);

    public string ConfigVersion => _status?.ConfigVersion?.ToString(CultureInfo.CurrentCulture) ?? "-";

    /// <summary>
    /// What the instance at the other end is running, on one line.
    /// </summary>
    /// <remarks>
    /// One row and not two. The two numbers are quoted together -- down a
    /// telephone, into an issue, at the top of a support mail -- and a second
    /// label nearly as long as its value would buy nothing for the line it
    /// costs on a page that already has eight.
    /// <para>
    /// The silence is named rather than drawn as a dash. Most of the fleet
    /// will be on an ADL older than the release that added the block, so this
    /// wording is what most machines show for a while, and it is worth
    /// something: "too old to say" puts a lower bound on the far end's
    /// version, which is a fact about the far end. A "-" here would read as
    /// this machine having failed to fetch something.
    /// </para>
    /// <para>
    /// Before the first sync there is no answer of either kind, so it falls
    /// back to the dash every other unanswered row on this page uses -- the
    /// grid it sits in is hidden until this machine has paired anyway.
    /// </para>
    /// </remarks>
    public string AdlVersion
    {
        get
        {
            if (_status is null)
            {
                return "-";
            }

            if (!_status.AdlReportedItsVersion)
            {
                return _status.LastSyncedAt is null
                    ? "-"
                    : "Not reported — this ADL predates the field";
            }

            var adl = Blank(_status.AdlVersion);
            var plugin = Blank(_status.PluginVersion);

            return $"{adl}  ·  agent plugin {plugin}";

            // Half an answer is still an answer, and is shown as one. An
            // instance that sent the block with one string empty has told us
            // something is wrong at its end, not at this one.
            static string Blank(string value) =>
                string.IsNullOrWhiteSpace(value) ? "unknown" : value;
        }
    }

    public string CheckInterval => _status is null
        ? "-"
        : string.Create(CultureInfo.CurrentCulture, $"every {_status.CheckIntervalMinutes} minutes");

    /// <summary>True while this machine is collecting something.</summary>
    /// <remarks>
    /// What the header hangs its one live fact on. A machine between passes
    /// -- which a settled one nearly always is -- shows nothing, because a
    /// line reading "collecting 0 stations" is a line that teaches a reader
    /// to stop looking at it.
    /// </remarks>
    public bool Collecting => _status is { CollectingStations: > 0 };

    /// <summary>
    /// What this machine is collecting at this moment, in words.
    /// </summary>
    /// <remarks>
    /// The answer to the question the window could not answer before: a
    /// server working through a first bind's backlog spends hours uploading,
    /// and everything else on this screen is a stale count or a cadence.
    /// Somebody looking at it could not tell that machine from one that had
    /// stopped -- which is exactly what happened, at a country that had just
    /// bound four stations with years of history behind them.
    /// </remarks>
    public string CollectingNow => _status is not { CollectingStations: > 0 }
        ? ""
        : _status.CollectingStations == 1
            ? "collecting 1 station"
            : string.Create(
                CultureInfo.CurrentCulture,
                $"collecting {_status.CollectingStations} stations");

    public string ClockSkew => _status?.ClockSkewSeconds is null
        ? "-"
        : string.Create(CultureInfo.CurrentCulture, $"{_status.ClockSkewSeconds} seconds");

    /// <summary>
    /// How often a station offers its whole folder, in the words the sweep
    /// itself would use.
    /// </summary>
    /// <remarks>
    /// Read through <see cref="ReconciliationSweep.Interval"/> rather than
    /// off the number, so the window cannot say "every 24 hours" about a
    /// machine whose sweep has been switched off, or disagree with the clamp
    /// on a cadence ADL typed as a decade.
    /// <para>
    /// "Never" is not a fault and is not shown as one. A deployment whose
    /// link cannot carry a full folder's manifest is entitled to switch
    /// sweeps off, and the technician standing here should be able to see
    /// that it was switched off on purpose rather than wonder why an old file
    /// never went.
    /// </para>
    /// </remarks>
    public string Reconciles
    {
        get
        {
            if (_status is null)
            {
                return "-";
            }

            var interval = ReconciliationSweep.Interval(_status.ReconciliationIntervalHours);

            return interval is null ? "never" : Display.Every(interval.Value);
        }
    }

    /// <summary>What the agent last saw go wrong, or that nothing has.</summary>
    /// <remarks>
    /// A word rather than an empty row. A blank beside a label reads as a
    /// value the service failed to send, which is the opposite of what it
    /// means here -- the same mistake <see cref="AdlUrl"/> two dozen lines
    /// above was fixed for.
    /// </remarks>
    public string LastError => string.IsNullOrWhiteSpace(_status?.LastError)
        ? "None"
        : _status!.LastError!;

    /// <summary>
    /// True when the header may show the four facts ADL supplies.
    /// </summary>
    /// <remarks>
    /// The version beside them is this machine's own and is not gated: it is
    /// true from the moment the service answers, and it is the first thing HQ
    /// asks for down a telephone. The other four are ADL's, and a header that
    /// went on saying "scans every 10 minutes" about a machine ADL has never
    /// told anything to scan would be inventing one.
    /// <para>
    /// Gated with the rows on the Status tab and by the same question, so the
    /// page and the strip three inches above it cannot disagree about whether
    /// there is anything to say yet.
    /// </para>
    /// </remarks>
    public bool ShowsAdlFacts => HasEverPaired;

    /// <summary>
    /// True when the header's first line is the address rather than a
    /// sentence about the state.
    /// </summary>
    /// <remarks>
    /// The paired machine is the only one with an address worth clicking, and
    /// the only one whose headline is somewhere else -- the dot two lines
    /// below it. In every other state the first line <em>is</em> the headline,
    /// so the window draws <see cref="Headline"/> there instead and gives it
    /// the weight this one does not get.
    /// </remarks>
    public bool ShowsPairedTo => IsPaired && !NeedsRePairing;

    /// <summary>
    /// True when the header's first line is <see cref="Headline"/>'s
    /// sentence rather than the address.
    /// </summary>
    /// <remarks>
    /// The exact complement of <see cref="ShowsPairedTo"/>, and a property
    /// rather than an inverting converter in the window because every other
    /// gate on this screen is a bool the view model decided. One of the two
    /// rows is always drawn and never both.
    /// </remarks>
    public bool ShowsHeadline => !ShowsPairedTo;

    /// <summary>
    /// How long ago the last heartbeat was, parenthesised, or nothing.
    /// </summary>
    /// <remarks>
    /// Beside <see cref="LastHeartbeat"/> rather than folded into it. The
    /// exact moment is what somebody reads down a telephone or matches
    /// against a log; the span is the reading they do at a glance, and a
    /// machine whose heartbeat is three days old should not need arithmetic
    /// to say so.
    /// <para>
    /// The brackets are in the string rather than around it in the window,
    /// for the reason the header strip was assembled here before it: a
    /// <c>Run</c> cannot be hidden -- it is not an element -- so a machine ADL
    /// has never beaten for would be left showing an empty pair of them.
    /// </para>
    /// </remarks>
    public string LastHeartbeatAgo => Bracketed(_status?.LastHeartbeatAt);

    /// <summary>
    /// How long ago the last configuration sync was, parenthesised, or
    /// nothing.
    /// </summary>
    public string LastSyncedAgo => Bracketed(_status?.LastSyncedAt);

    /// <summary>A moment as "(3 hours ago)", or nothing at all.</summary>
    private string Bracketed(DateTimeOffset? moment) => moment is { } value
        ? string.Create(CultureInfo.CurrentCulture, $"({Display.Ago(value, _asOf)})")
        : "";

    /// <summary>
    /// Which of the three colours ADL's verdict is worth, as a word the
    /// window has a trigger for.
    /// </summary>
    /// <remarks>
    /// The same vocabulary as <see cref="TrayState"/> and the connection
    /// rows' <c>Attention</c>, because green, amber and red already mean
    /// something on this screen and a fourth reading of them would be a
    /// fourth thing to learn.
    /// <para>
    /// <c>offline</c> is red and <c>cycle_stuck</c> is amber, which is the one
    /// pair worth arguing about: both mean nothing is arriving, but a machine
    /// that is still heartbeating is one somebody can still reach, and a
    /// machine that has gone silent is one somebody has to walk to.
    /// </para>
    /// <para>
    /// Grey for a state this build has never heard of, matching the rule the
    /// station grid already keeps: grey is the absence of a verdict rather
    /// than a fourth one, and a build that does not know what ADL just said
    /// has no verdict to offer.
    /// </para>
    /// </remarks>
    public TrayState FleetTone
    {
        get
        {
            var state = _status?.FleetStatus;

            if (string.IsNullOrWhiteSpace(state))
            {
                return TrayState.Unknown;
            }

            return FleetTones.TryGetValue(state, out var tone) ? tone : TrayState.Unknown;
        }
    }

    /// <summary>
    /// What this machine is doing about updating itself, in one sentence.
    /// </summary>
    /// <remarks>
    /// ADL's own words wherever it had any: it is the instance that knows
    /// whether it is holding nothing, or holding everything except the
    /// version this machine has been pinned to, and a sentence assembled
    /// here would only ever be a worse guess at the same thing.
    /// </remarks>
    public string UpdateStatus
    {
        get
        {
            if (_status is null)
            {
                return "-";
            }

            if (!string.IsNullOrWhiteSpace(_status.UpdateDetail))
            {
                return _status.UpdateDetail;
            }

            return _status.UpdateCheckedAt is null
                ? "Not checked yet."
                : "Up to date.";
        }
    }

    /// <summary>
    /// What this machine <em>is</em>, in one sentence -- for the four states
    /// that need one, and nothing at all for the one that does not.
    /// </summary>
    /// <remarks>
    /// Only what it is. What to do about it is <see cref="NextStep"/>, which
    /// is on the screen directly beneath this, and a header that also gave
    /// the instruction would be the same sentence twice -- and, worse, two
    /// copies of it to drift apart. This one describes; the one below acts.
    /// <para>
    /// Empty on a working machine, and that is the whole of the header's
    /// arrangement rather than an omission. The four states below are ones a
    /// technician has to be told about in words; a paired machine's headline
    /// is ADL's verdict, which the header draws two lines lower with a colour
    /// beside it, and repeating the address there in a sentence would be the
    /// same fact twice with only one of them clickable.
    /// </para>
    /// <para>
    /// No longer the tray icon's tooltip. That shows ADL's verdict alone --
    /// see <c>App.OnStatus</c> -- so the two are free to differ and this one
    /// is now the window's.
    /// </para>
    /// </remarks>
    public string Headline
    {
        get
        {
            if (!_serviceReached)
            {
                return "The ADL Agent service is not running on this machine.";
            }

            if (!IsConfigured)
            {
                return "No ADL address is configured on this machine.";
            }

            if (NeedsRePairing)
            {
                return "ADL has revoked this machine's token, so nothing is being sent.";
            }

            if (!IsPaired)
            {
                return "This machine is not paired with ADL yet.";
            }

            return "";
        }
    }

    /// <summary>
    /// What to do now, and who has to do it: the line at the top of every
    /// tab.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same as <see cref="Headline"/>, which says what
    /// this machine <em>is</em>. Both are always there: a technician looking
    /// at a working machine should read "nothing to do" rather than have to
    /// infer it from the absence of a banner, and the state a banner appears
    /// in is not the only state somebody needs telling about.
    /// </remarks>
    public NextStep NextStep
    {
        get => _nextStep;
        private set
        {
            if (Set(ref _nextStep, value))
            {
                Raise(nameof(NoStationsReason));
            }
        }
    }

    /// <summary>
    /// Which tab the window is on. Bound both ways: chosen once from the
    /// machine's state, and the technician's from then on.
    /// </summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set => Set(ref _selectedTab, value);
    }

    /// <summary>
    /// True when there is nothing at all in the left-hand list.
    /// </summary>
    /// <remarks>
    /// The tab-wide emptiness, and the only one that is about the whole
    /// machine: the service is not running, this machine is not paired, ADL
    /// is not answering, or an administrator has linked nothing here. A
    /// connection that merely has no stations of its own is a fact about one
    /// row, and says so beside that row rather than across the tab.
    /// <para>
    /// About the rows on the screen and not about
    /// <see cref="NextStep"/>'s view of what ADL holds, because it decides
    /// whether to draw a sentence in place of a list. The two can disagree
    /// for a moment -- ADL dropping everything while somebody is typing into
    /// a row leaves the rows there, on purpose -- and when they do, the right
    /// thing is to leave the rows a technician is working in alone and let
    /// the line say what has happened.
    /// </para>
    /// </remarks>
    public bool HasNoConnections => Connections.Count == 0;

    /// <summary>
    /// True when the tab should explain the machine instead of drawing a
    /// list.
    /// </summary>
    /// <remarks>
    /// Either there is nothing to draw, or what would be drawn is a memory:
    /// during an outage the connections come off the disk, and a cached
    /// connection with nothing under it would otherwise announce that an
    /// administrator has not linked anything -- true of the last sync, and
    /// exactly the wrong person to send somebody to find while the network
    /// is what is broken.
    /// </remarks>
    public bool ShowsMachineReason => HasNoConnections || NextStep.ListIsStale;

    /// <summary>
    /// True when the station list is a tab a technician can open at all.
    /// </summary>
    /// <remarks>
    /// The rule is <see cref="TrayTabs.Available"/>'s, so that both things
    /// this window decides about tabs -- which one to open on, and whether
    /// each can be opened -- are decided in the same file over the same
    /// facts.
    /// <para>
    /// Read off <see cref="NextStep"/> rather than off the connection list,
    /// although a never-paired machine has neither. They come apart in the
    /// state that matters: a paired machine whose ADL has linked nothing to
    /// it yet also has no connections, and that tab must open -- an empty
    /// list with a sentence saying who to ask is the entire answer for that
    /// machine.
    /// </para>
    /// </remarks>
    public bool StationsAvailable => TrayTabs.Available(NextStep);

    /// <summary>
    /// True when the selected connection should explain its own empty grid.
    /// </summary>
    /// <remarks>
    /// Only when the machine has nothing to say first. The two sentences are
    /// answers to different questions and never both right at once: one is
    /// about this machine, the other about the row somebody just clicked.
    /// </remarks>
    public bool ShowsConnectionReason =>
        !ShowsMachineReason && SelectedConnection?.HasNoStations == true;

    /// <summary>
    /// Why the station list is empty, in the words of the reason it is.
    /// </summary>
    /// <remarks>
    /// "ADL has linked nothing to this device yet", "ADL is not answering" and
    /// "the service is not running" are three different problems wanting three
    /// different people, and until this they were the same empty grid.
    /// <para>
    /// The states that leave no list to explain -- a folder to bind, a station
    /// that collected nothing -- carry no sentence of their own, and fall back
    /// to the line. They cannot be reached with an empty grid, and a fallback
    /// costs nothing where an invariant nobody restates would eventually cost
    /// a blank rectangle.
    /// </para>
    /// </remarks>
    public string NoStationsReason =>
        NextStep.NoStations.Length > 0 ? NextStep.NoStations : NextStep.Text;

    /// <summary>
    /// The answer to the last thing a button did.
    /// </summary>
    /// <remarks>
    /// Internal rather than private to set, because the settings window's view
    /// model writes it too -- deliberately the same string rather than one of
    /// its own, so that a refusal read in front of the window and a success
    /// read behind it are the same sentence.
    /// </remarks>
    public string Message
    {
        get => _message;
        internal set => Set(ref _message, value);
    }

    public string PairingCode
    {
        get => _pairingCode;
        set
        {
            if (Set(ref _pairingCode, value))
            {
                PairCommand.Refresh();
            }
        }
    }

    /// <summary>
    /// Which connection is highlighted, and so whose stations the grid shows.
    /// </summary>
    /// <remarks>
    /// Changing it moves to that connection's first station rather than
    /// leaving nothing selected. Without that, every click on the left lands
    /// on a grid with no row selected, and a technician has to click twice to
    /// do anything -- which is the same reason <see cref="Show"/> falls back
    /// to the first station when it rebuilds.
    /// </remarks>
    public ConnectionViewModel? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            // Before the change test, and deliberately: what dismisses the
            // hint is somebody having chosen, and a click that lands on the
            // connection already selected is still a person having understood
            // what the pane is for.
            if (!_restoring && !_connectionClicked)
            {
                _connectionClicked = true;

                Raise(nameof(ShowsConnectionHint));
            }

            if (!Set(ref _selectedConnection, value))
            {
                return;
            }

            SelectedStation = value?.Stations.FirstOrDefault();

            Raise(nameof(HasSelectedConnection));
            Raise(nameof(ShowsConnectionReason));
            Raise(nameof(StationsHeading));
        }
    }

    /// <summary>
    /// True while the pane should still be telling somebody what it is for.
    /// </summary>
    /// <remarks>
    /// Shown until a technician picks a connection themselves, and then never
    /// again for the life of the tray. The pane is a master list beside a
    /// detail grid, which is an idiom -- but it is one this window did not
    /// have until recently, and the person it was split for opens it once, on
    /// a country server, having been told to on the telephone.
    /// <para>
    /// It cannot simply be "nothing is selected yet": <see cref="Choose"/>
    /// picks a connection from the machine's first answer, so there is no
    /// moment after the window is drawn when nothing is. Hence
    /// <see cref="_restoring"/>, which is what lets this class tell its own
    /// selection from a person's.
    /// </para>
    /// </remarks>
    public bool ShowsConnectionHint => !_connectionClicked && Connections.Count > 0;

    /// <summary>
    /// What the station grid is a list of, above it.
    /// </summary>
    /// <remarks>
    /// Named rather than left implicit because the grid no longer carries a
    /// Connection column -- every row in it is from the connection selected on
    /// the left, which was 130 pixels repeating the selection back -- and a
    /// grid whose scope is only stated by a highlight in another control is a
    /// grid somebody can read the wrong connection's stations out of.
    /// </remarks>
    public string StationsHeading => SelectedConnection is { } connection
        ? string.Create(CultureInfo.CurrentCulture, $"Station links for {connection.ConnectionName}")
        : "";

    public bool HasSelectedConnection => SelectedConnection is not null;

    /// <summary>
    /// Which row is highlighted, and so which station the settings window
    /// would open on.
    /// </summary>
    /// <remarks>
    /// A selection and nothing more. Rows are never typed into -- editing
    /// happens on a copy in a window of its own -- so there is no edit here
    /// to preserve, notify about, or lose.
    /// </remarks>
    public StationViewModel? SelectedStation
    {
        get => _selectedStation;
        set
        {
            if (Set(ref _selectedStation, value))
            {
                Raise(nameof(HasSelectedStation));
            }
        }
    }

    public bool HasSelectedStation => SelectedStation is not null;

    // ---------- the five things this window can ask for ----------

    /// <summary>Read the machine's standing and its station list.</summary>
    /// <remarks>
    /// Both, in that order, on every refresh. The list is small (a device
    /// serves tens of stations, not thousands) and the two are read together
    /// so the window cannot draw a station list beside a status that
    /// disagrees with it.
    /// </remarks>
    public async Task RefreshAsync()
    {
        var status = await _agent.StatusAsync().ConfigureAwait(true);

        _serviceReached = status.ServiceReached;
        _status = status.Value ?? _status;

        // This machine's clock until the service supplies its own, below. A
        // header that kept the previous answer's "now" would stop aging the
        // moment the service went away -- which is the moment the age of the
        // last heartbeat starts being the interesting number.
        _asOf = DateTimeOffset.UtcNow;

        if (status.ServiceReached)
        {
            var stations = await _agent.StationsAsync().ConfigureAwait(true);

            if (stations.Value is not null)
            {
                _linked = stations.Value.Stations;
                _asOf = stations.Value.AsOf;

                Show(stations.Value);
            }
        }

        // Before the line, because the answer to a press is the more urgent
        // of the two and the line is about to be rewritten from the same
        // snapshot.
        Synced();

        // Last, and on every path through the method above -- including the
        // ones that changed no row. This is the poll, and the line at the top
        // of the window is what tells a technician that waiting was the right
        // thing to be doing. It has to move on its own or it is not one.
        Restate();
    }

    /// <summary>
    /// Ask ADL for this device's configuration now (Sync with ADL, above
    /// the connection list).
    /// </summary>
    /// <remarks>
    /// The agent starts the call and answers at once, so this method returns
    /// long before ADL has said anything. That is deliberate: the control pipe
    /// serves one client at a time and times out in three seconds, and a
    /// window that waited on an HTTP call over these links would freeze its own
    /// header and then report a working service as absent.
    /// <para>
    /// What is remembered is the moment the attempt started, which is the only
    /// thing that lets the poll below tell this press from the one before it,
    /// or from a sync some other logon session's tray asked for.
    /// </para>
    /// </remarks>
    public async Task SyncAsync()
    {
        var started = await _agent.SyncAsync().ConfigureAwait(true);

        if (started.Value is null)
        {
            Message = started.Detail ?? "The agent could not ask ADL for the configuration.";

            return;
        }

        _awaitedSync = started.Value.StartedAt;

        Message = "Asking ADL for the latest configuration…";

        SyncCommand.Refresh();
    }

    /// <summary>
    /// Say what the sync this window asked for came to, once it has come to
    /// anything.
    /// </summary>
    /// <remarks>
    /// Read off the status the window is already polling rather than waited
    /// for, so nothing about this holds the pipe. Matched on the moment it
    /// started, so a press whose attempt the service has since replaced -- a
    /// restart, another session -- stops being waited for rather than waiting
    /// for ever.
    /// </remarks>
    private void Synced()
    {
        if (_awaitedSync is not { } awaited)
        {
            return;
        }

        var attempt = _status?.RequestedSync;

        if (attempt?.StartedAt != awaited)
        {
            // Replaced by somebody else's, or lost to a restart. Either way
            // this window's answer is never coming, and a Refresh button grey
            // for the rest of the session would be the worse outcome.
            _awaitedSync = null;

            SyncCommand.Refresh();

            return;
        }

        if (attempt.FinishedAt is null)
        {
            return;
        }

        _awaitedSync = null;

        Message = attempt.Ok
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"Synced with ADL. Configuration is now at version {attempt.ConfigVersion}.")
            : attempt.Detail ?? "ADL did not answer.";

        SyncCommand.Refresh();
    }

    /// <summary>Redeem a pairing code (story 2).</summary>
    public async Task PairAsync()
    {
        var paired = await _agent.PairAsync(PairingCode.Trim()).ConfigureAwait(true);

        if (!paired.Ok)
        {
            Message = paired.Detail ?? "The agent refused that pairing code.";

            return;
        }

        PairingCode = "";
        _pairAgain = false;
        Message = string.Create(
            CultureInfo.CurrentCulture,
            $"Paired. ADL knows this machine as {paired.Value!.DeviceName}.");

        _status = paired.Value;
        _serviceReached = true;

        Restate();

        await RefreshAsync().ConfigureAwait(true);
    }

    // ---------- changing where this machine reports ----------

    /// <summary>
    /// Open the ADL address for editing, or return null before the service
    /// has said what it is.
    /// </summary>
    /// <remarks>
    /// The same two moves as <see cref="BeginEditing"/>, for the same two
    /// reasons: the address is copied into a dialog rather than edited in
    /// place, and the poll stops rebuilding rows while that dialog is over
    /// the window.
    /// <para>
    /// The address itself rather than what the row draws. <see cref="AdlUrl"/>
    /// renders a machine with none as a sentence, and a sentence in a box
    /// somebody is about to save would be an address nothing could reach.
    /// </para>
    /// </remarks>
    public AdlAddressViewModel? BeginChangingAdl()
    {
        if (_status is null)
        {
            return null;
        }

        _editing = true;
        Message = "";

        return new AdlAddressViewModel(this, _status.AdlUrl);
    }

    /// <summary>
    /// Point this machine at <paramref name="address"/>, through Windows'
    /// own consent (story 27).
    /// </summary>
    /// <remarks>
    /// A thin caller of <c>adl-agent set-url</c>, and thin on purpose: the
    /// verb validates, stops the service, writes the file, drops the pairing
    /// and starts the service again, so a machine repointed from this window
    /// and one repointed from a command prompt end up in the same state by
    /// the same code.
    /// <para>
    /// The one thing decided before asking is whether the address is usable,
    /// and it is decided with the agent's own rule rather than a second one
    /// written here. Raising a consent prompt, taking an administrator's
    /// password, and then reporting that the address was never going to work
    /// is the one bad outcome this window can prevent by itself -- and the
    /// check is free, because <see cref="AgentOptions"/> is the thing the
    /// service will bind the file to at start-up.
    /// </para>
    /// </remarks>
    public async Task<AddressChangeOutcome> ChangeAdlAddressAsync(string address, bool keepPairing)
    {
        var url = address.Trim();

        if (AgentOptions.ProblemWith(url) is { } problem)
        {
            Message = problem;

            return AddressChangeOutcome.Refused;
        }

        var answer = await _adlAddress.RequestAsync(url, keepPairing).ConfigureAwait(true);

        if (answer.Outcome != AddressChangeOutcome.Changed)
        {
            Message = NotChanged(answer);

            return answer.Outcome;
        }

        Message = Pointed(url, keepPairing);

        // What the verb just did, applied to the window's copy of the machine
        // until the service answers with its own. The alternative is a page
        // that goes on saying "Paired" beside a line saying the pairing was
        // cleared, for as long as the restarting service takes to answer --
        // and a poll that cannot reach it keeps the last snapshot, so "as
        // long as" has no upper bound. The window would be pretending the
        // machine is one it knows it is not, which is the one thing this
        // button must never do.
        //
        // Not an invention: it is the outcome of something this window asked
        // for and was told succeeded, which is exactly what PairAsync does
        // with the status a redeemed code answers with.
        _status = Repointed(_status, url, keepPairing);

        if (!keepPairing)
        {
            // The tab as well as the state, because ChooseTab has long since
            // made its one choice. The one thing to do about this machine is
            // the code box on this tab.
            SelectedTab = TrayTabs.Status;
        }

        Restate();

        return answer.Outcome;
    }

    /// <summary>
    /// The machine as the verb has just left it: a new address, and -- unless
    /// the pairing was kept -- no pairing.
    /// </summary>
    /// <remarks>
    /// Only the facts the verb changed. Everything else on the snapshot is
    /// the last thing the service said and stays that way, including the
    /// heartbeat and the cycle counts: they describe what this machine did
    /// before it was moved, which is still what it did.
    /// <para>
    /// The address is known configured because nothing reaches here that
    /// <see cref="AgentOptions.ProblemWith"/> refused, so the hint and the
    /// problem go with it.
    /// </para>
    /// </remarks>
    private static AgentStatusSnapshot? Repointed(
        AgentStatusSnapshot? status, string url, bool keepPairing)
    {
        if (status is null)
        {
            return null;
        }

        var pointed = status with
        {
            AdlUrl = url,
            Configured = true,
            ConfigurationProblem = null,
            ConfigurationHint = null,
        };

        return keepPairing
            ? pointed
            : pointed with
            {
                PairingState = nameof(CorePairingState.Unpaired),
                RePairNeeded = false,
                DeviceId = null,
                DeviceName = null,
                PairedAt = null,
            };
    }

    /// <summary>What to say about an address that did not move.</summary>
    /// <remarks>
    /// A declined prompt is told apart from everything else here and nowhere
    /// else, because it is the one refusal that is somebody's decision rather
    /// than a fault -- and the one where saying nothing at all would let a
    /// window read as though the change had gone through. What every other
    /// refusal has is a sentence of its own, from whichever of the two knows:
    /// the agent's rule, or the verb's exit code.
    /// </remarks>
    private static string NotChanged(AddressChange answer)
    {
        var said = string.IsNullOrWhiteSpace(answer.Detail) ? "" : answer.Detail.Trim() + " ";

        return answer.Outcome == AddressChangeOutcome.Declined
            ? said + "Nothing has been changed on this machine."
            : said.Length > 0 ? said.TrimEnd() : "The address was not changed.";
    }

    /// <summary>
    /// What to say about an address that moved, in the two tenses its pairing
    /// leaves it in.
    /// </summary>
    /// <remarks>
    /// Both name the address, because the row above is still showing the old
    /// one until the restarting service answers the next poll. What differs is
    /// the instruction: one machine has to be paired again before anything is
    /// sent, and the other only if the new ADL refuses the token it kept.
    /// </remarks>
    private static string Pointed(string url, bool keepPairing) => keepPairing
        ? string.Create(
            CultureInfo.CurrentCulture,
            $"This machine now reports to {url}, keeping the pairing it had. If ADL refuses the "
            + $"token, pair this machine again.")
        : string.Create(
            CultureInfo.CurrentCulture,
            $"This machine now reports to {url}. Its pairing was cleared: paste a pairing code "
            + $"from the new ADL below.");

    // ---------- editing one station, in a window of its own ----------

    /// <summary>
    /// Begin editing the selected station, or return null when no row is
    /// selected.
    /// </summary>
    /// <remarks>
    /// Two things happen here, and they are the same decision seen from two
    /// sides. The station is <em>copied</em> (see
    /// <see cref="StationViewModel.Editing"/>), so what is typed into is not
    /// the row; and the poll stops rebuilding rows until
    /// <see cref="EndEditing"/>, so the row cannot be replaced underneath the
    /// copy's station identity while somebody is working.
    /// <para>
    /// The message is cleared because whatever it last said was the answer to
    /// something else, and the window about to open renders it.
    /// </para>
    /// </remarks>
    public StationSettingsViewModel? BeginEditing(FolderChoice folders)
    {
        if (SelectedStation is not { } selected)
        {
            return null;
        }

        _editing = true;
        Message = "";

        return new StationSettingsViewModel(this, selected.Editing(folders));
    }

    /// <summary>The settings window has closed; the rows may move again.</summary>
    public void EndEditing() => _editing = false;

    /// <summary>
    /// Open the selected station's status, or return null when no row is
    /// selected.
    /// </summary>
    /// <remarks>
    /// The same two moves as <see cref="BeginEditing"/>, for the same two
    /// reasons, even though nothing here is typed into. The station is copied
    /// so the row is not the object the window probes against -- a probe
    /// writes a sentence onto the station it probed, and the row behind should
    /// go on saying what ADL sent -- and the poll stops rebuilding rows, so
    /// the window cannot end up describing a station the list no longer
    /// contains.
    /// </remarks>
    public StationStatusViewModel? BeginWatching()
    {
        if (SelectedStation is not { } selected)
        {
            return null;
        }

        _editing = true;
        Message = "";

        return new StationStatusViewModel(this, selected.Probing());
    }

    /// <summary>
    /// Start collecting the selected station now, or say why that could not
    /// happen.
    /// </summary>
    /// <remarks>
    /// Returns null on every refusal, and writes the service's own sentence
    /// into <see cref="Message"/> rather than one of its own. The three
    /// reasons -- a cycle already running, a station switched off in ADL, a
    /// station with no folder bound -- are things the service knows and this
    /// window would only be guessing at: HQ can switch a station off between
    /// the row being drawn and the item being pressed, and a menu item greyed
    /// from a stale row is not a check.
    /// <para>
    /// The rows stop rebuilding only once a run is actually under way. A
    /// refusal leaves the window exactly as it was, and freezing the list
    /// behind a modal window that never opened is how a station list comes to
    /// be frozen for the rest of the session.
    /// </para>
    /// </remarks>
    public async Task<CollectViewModel?> BeginCollectingAsync()
    {
        if (SelectedStation is not { } selected)
        {
            return null;
        }

        Message = "";

        var started = await _agent.CollectAsync(selected.StationLinkId).ConfigureAwait(true);

        if (started.Value is null)
        {
            Message = started.Detail ?? "The agent would not collect that station now.";

            return null;
        }

        _editing = true;

        return new CollectViewModel(_agent, started.Value);
    }

    /// <summary>
    /// The last few passes this station has been in, as headings.
    /// </summary>
    /// <remarks>
    /// Three lines and no file detail, because this is the at-a-glance half
    /// of the answer: "has anything happened here lately". Anything more is
    /// what View more is for, and going through the light index means this
    /// never fetches a record it is not going to show.
    /// <para>
    /// Read through the service like every other fact this window shows. The
    /// records are in a folder whose permissions are SYSTEM and
    /// Administrators and the tray runs as whoever is logged in, so a window
    /// that read them itself would work for the developer and for nobody in
    /// a ministry.
    /// </para>
    /// </remarks>
    public async Task<PassesAnswer> RecentPassesAsync(long stationLinkId, int most = 3)
    {
        var answer = await _agent
            .PassesAsync(new CyclePassQuery(stationLinkId, Most: most))
            .ConfigureAwait(true);

        if (answer.Value is null)
        {
            return new PassesAnswer(
                [],
                false,
                answer.Detail ?? "The agent could not read this machine's record of what it has done.");
        }

        return new PassesAnswer(
            answer.Value.Rows.Select(row => new PassRowViewModel(row)).ToList(),
            !answer.Value.Exhausted,
            null);
    }

    /// <summary>
    /// Open the machine's own record of what it has collected.
    /// </summary>
    /// <remarks>
    /// Modeless and not counted as editing, unlike every other window this
    /// class opens. Those hold a copy of a station row and freeze the list so
    /// they cannot end up describing a station that has gone; this holds no
    /// row, and a technician wants to press Collect now on the list behind it
    /// and then look here for what it did.
    /// </remarks>
    /// <param name="stationLinkId">
    /// The station to open filtered to, or <c>null</c> for the machine's own
    /// passes.
    /// </param>
    public PassesViewModel Passes(long? stationLinkId = null) => new(
        _agent,
        _status?.DeviceName ?? "this machine",
        Connections
            .SelectMany(connection => connection.Stations)
            .Select(station => new StationChoice(
                station.StationLinkId,
                string.IsNullOrWhiteSpace(station.StationName)
                    ? station.StationLinkId.ToString(CultureInfo.CurrentCulture)
                    : station.StationName))
            .ToList(),
        stationLinkId);

    /// <summary>
    /// Have the agent write a diagnostics bundle, and say what happened.
    /// </summary>
    /// <remarks>
    /// The agent writes it and this only chooses where, which is the only
    /// arrangement that works on a machine an administrator has installed
    /// properly: the logs are where the token is, and the tray cannot read
    /// that folder.
    /// </remarks>
    /// <param name="passes">
    /// Which passes the bundle should carry -- the filter the technician was
    /// looking at, when this was pressed from the passes window. Left null
    /// from the Status tab, where the question is about the machine.
    /// </param>
    public async Task SaveDiagnosticsAsync(string path, CyclePassQuery? passes = null)
    {
        // Said before the wait rather than after it. The agent flushes both
        // logs and then reads a few hundred kilobytes off them, which on a
        // country server is long enough for a window that had not changed to
        // read as a button that did nothing.
        Message = "Collecting diagnostics…";

        var written = await _agent.SaveDiagnosticsAsync(path, passes).ConfigureAwait(true);

        Message = written.Value is null
            ? written.Detail ?? "The agent could not write a diagnostics file."
            : string.Create(
                CultureInfo.CurrentCulture,
                $"Diagnostics saved to {written.Value.Path} ({Kilobytes(written.Value.Bytes)}).");
    }

    /// <summary>
    /// A file size, as the sentence beside a saved file says one.
    /// </summary>
    /// <remarks>
    /// Worth saying at all because it is what tells a technician the file they
    /// are about to attach has something in it. A bundle of 900 bytes is a
    /// machine that has recorded nothing, and that is itself the answer.
    /// </remarks>
    private static string Kilobytes(long bytes) => bytes < 1024
        ? string.Create(CultureInfo.CurrentCulture, $"{bytes} bytes")
        : string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0} KB");

    /// <summary>
    /// Count what a station's boxes would match (story 7).
    /// </summary>
    /// <remarks>
    /// Called from a debounce in the settings window rather than on every
    /// keystroke: each call walks a folder that may hold a hundred thousand
    /// files.
    /// <para>
    /// The station is the caller's rather than the selection, and that is
    /// what removed the race this used to guard against. One station is being
    /// edited at a time, in a modal window, and it cannot change into another
    /// one while an answer is in flight.
    /// </para>
    /// </remarks>
    public async Task CountMatchesAsync(StationViewModel station)
    {
        var counted = await _agent.PreviewAsync(station.PreviewRequest()).ConfigureAwait(true);

        if (counted.Value is null)
        {
            station.CouldNotCount(counted.Detail ?? "The agent could not count these settings.");

            return;
        }

        station.Counted(counted.Value);
    }

    /// <summary>
    /// Write a station's changed settings through to ADL (story 9), and say
    /// which of the three things happened.
    /// </summary>
    /// <remarks>
    /// No refresh here, unlike the version this replaced. The settings window
    /// closes on everything except a refusal, and the refresh happens after
    /// it has -- so a rebuild of the rows can never land under a window that
    /// is still open, and the rebuild that follows a save needs no permission
    /// to replace an edit because there is no longer an edit to replace.
    /// </remarks>
    public async Task<SaveOutcome> SaveStationAsync(StationViewModel station)
    {
        var changes = station.Changes();

        if (changes.Count == 0)
        {
            // Not reachable from the button, which is disabled until
            // something differs. Reachable from a save that raced an edit
            // being undone, and a refusal is the honest answer to it.
            Message = "Nothing has changed.";

            return SaveOutcome.Refused;
        }

        var written = await _agent.ConfigureAsync(station.StationLinkId, changes).ConfigureAwait(true);

        if (!written.Ok)
        {
            Message = written.Detail ?? "ADL would not accept those settings.";

            // A revoked token is not a refusal to fix in this window: nothing
            // typed in it can be saved by anybody until the machine is paired
            // again. The window closes on this, and the next-step line behind
            // it -- drawn from what the service holds, which is why this reads
            // it back now rather than waiting for the poll -- says what to do.
            if (written.NeedsRePairing)
            {
                await RefreshAsync().ConfigureAwait(true);

                return SaveOutcome.MustRePair;
            }

            return SaveOutcome.Refused;
        }

        Message = string.Create(
            CultureInfo.CurrentCulture,
            $"Saved to ADL. Configuration is now at version {written.Value!.ConfigVersion}.");

        return SaveOutcome.Saved;
    }

    /// <summary>
    /// Replace the connections and their rows with what the service just
    /// said, keeping the selection -- but only when they have changed and no
    /// settings window is open over this one.
    /// </summary>
    /// <remarks>
    /// Both guards are about the poll rather than about pressing Refresh.
    /// This runs every few seconds for as long as the tray is running.
    /// <para>
    /// Nothing is rebuilt while somebody is editing, because the settings
    /// window's station is one of these objects' twin and replacing the row
    /// halfway through would leave the window editing a station the list no
    /// longer contains. Suppressing the rebuild is a stronger rule than
    /// checking whether anything has been typed yet -- which was the previous
    /// one, and which was blind to the moment between opening a window and
    /// touching a box. The rest of the poll is untouched: the header, the
    /// next-step line and therefore the colour of the icon in the corner all
    /// go on moving while a window is open, because a technician who leaves
    /// one open should not be watching a tray icon that has quietly stopped
    /// telling the truth.
    /// </para>
    /// <para>
    /// The comparison is against the connections and stations and not the
    /// whole answer, which also carries the moment of the last sync and the
    /// last cycle. Those move on every cycle without anything having changed,
    /// and rebuilding on them would move the highlighted row out from under
    /// somebody about to press Edit settings.
    /// </para>
    /// <para>
    /// What it does compare is the two lists as they arrived, rather than a
    /// hand-listed set of fields, so a field added to either snapshot is
    /// noticed here without anybody remembering to add it.
    /// </para>
    /// </remarks>
    private void Show(AgentStationsSnapshot stations)
    {
        if (_editing)
        {
            return;
        }

        var arrived = JsonSerializer.Serialize(
            new { stations.Connections, stations.Stations }, AgentJson.Options);

        // Before the comparison, and deliberately outside it. How long a
        // station has been quiet moves with the clock and with nothing else,
        // so it is the one thing on a row that has to advance on a poll where
        // the machine said exactly what it said last time -- and the moment it
        // were inside `arrived`, every poll would rebuild every row.
        foreach (var row in Connections.SelectMany(connection => connection.Stations))
        {
            row.Aged(stations.AsOf);
        }

        if (arrived == _shown)
        {
            return;
        }

        _shown = arrived;

        var connection = SelectedConnection?.ConnectionId;
        var station = SelectedStation?.StationLinkId;

        // Every write to the selection below is this class restoring what was
        // already there, not a person choosing -- including the null, and
        // including the fall-backs at the end. See _restoring.
        _restoring = true;

        try
        {
            Rebuild(stations, connection, station);
        }
        finally
        {
            _restoring = false;
        }

        // After the rebuild rather than inside it: the hint is about whether
        // there is anything to click, and until the loop above has run there
        // is not.
        Raise(nameof(ShowsConnectionHint));
    }

    /// <summary>
    /// Replace the rows and put the selection back on what it was on.
    /// </summary>
    private void Rebuild(AgentStationsSnapshot stations, long? connection, long? station)
    {
        SelectedConnection = null;
        Connections.Clear();

        // Keyed rather than filtered per connection: a device may serve forty
        // stations across two connections, and this runs on every poll that
        // moved anything.
        var byConnection = stations.Stations
            .GroupBy(each => each.ConnectionId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AgentStationSnapshot>)[.. group]);

        foreach (var each in stations.Connections)
        {
            byConnection.TryGetValue(each.ConnectionId, out var owned);

            Connections.Add(new ConnectionViewModel(each, owned ?? [], stations.AsOf));
        }

        SelectedConnection =
            Connections.FirstOrDefault(each => each.ConnectionId == connection)
            ?? Choose(stations);

        // After the connection, which has already moved the selection to its
        // first station. Restoring the station the technician had is only
        // possible once the rows it might be among exist.
        SelectedStation =
            SelectedConnection?.Stations.FirstOrDefault(each => each.StationLinkId == station)
            ?? SelectedStation;
    }

    /// <summary>
    /// Which connection to open on, when there is no previous selection to
    /// restore.
    /// </summary>
    /// <remarks>
    /// The one the next-step line is pointing at, so a window that says
    /// "Bind a folder to Kakamega, under Vaisala AWS" opens already showing
    /// Vaisala AWS. The line names the connection because the list is split
    /// and the instruction has to be followable; this is the other half of
    /// the same fix, and it saves the technician the click the line just told
    /// them to make.
    /// <para>
    /// Once, and only from the first answer -- the same rule, for the same
    /// reason, as the tab. A pane that re-picked "the connection with work in
    /// it" on every poll would drag somebody off the connection they were
    /// reading, five seconds after they opened it, and would do it again
    /// every cycle. Getting it wrong costs one click; getting it wrong
    /// repeatedly costs the window's trustworthiness.
    /// </para>
    /// </remarks>
    private ConnectionViewModel? Choose(AgentStationsSnapshot stations)
    {
        if (_connectionChosen)
        {
            return Connections.FirstOrDefault();
        }

        _connectionChosen = true;

        // The same standing the line is written from, so the station it names
        // and the connection this opens on are the same station. Asked of the
        // standing directly rather than of the finished line, because the line
        // renders a station into a sentence and reading the connection back
        // out of a sentence is not a thing to do.
        var standing = StationStanding.Of(stations.Stations);

        var wanted = standing.Kind switch
        {
            StandingKind.BindAFolder => standing.Unbound[0].ConnectionId,
            StandingKind.FixAStation => standing.Failing[0].ConnectionId,
            StandingKind.Quiet => standing.Quiet[0].ConnectionId,

            // Nothing wants a person, so nothing is a better place to start
            // than the top of the list.
            _ => (long?)null,
        };

        return Connections.FirstOrDefault(each => each.ConnectionId == wanted)
            ?? Connections.FirstOrDefault();
    }

    /// <summary>
    /// Say that something in the window itself went wrong.
    /// </summary>
    /// <remarks>
    /// Public because the window has handlers of its own that are
    /// <c>async void</c> -- opening the settings window is one -- and nothing
    /// above such a handler can catch anything. An exception escaping one
    /// would end the process with no window and no message, on the machine
    /// where this program is the thing that explains what is wrong.
    /// </remarks>
    public void Failed(Exception exception) =>
        Message = $"Something went wrong in this window: {exception.Message}";

    /// <summary>
    /// Re-read everything the window draws from the last answer: the header,
    /// the next-step line, and -- once -- which tab to be on.
    /// </summary>
    private void Restate()
    {
        NextStep = NextSteps.For(_serviceReached, _status, _linked);

        // After the line, because the tab is read off it.
        //
        // And before the properties are raised, which is what keeps the tab
        // this window opens on and the tabs it allows from contradicting each
        // other for a frame. The window binds SelectedIndex two ways and starts
        // on Stations, so a poll that disabled Stations while it was still the
        // selected tab would have WPF move the selection itself and write the
        // move back through that binding. It cannot happen from here: the one
        // state that both disables Stations and arrives while it is selected
        // is the first answer from a never-paired machine, and ChooseTab moves
        // to Status on that same answer, so the two reach the window together.
        // Raising first would separate them.
        ChooseTab();

        foreach (var property in HeaderProperties)
        {
            Raise(property);
        }
    }

    /// <summary>
    /// Open on the tab that matches this machine, once.
    /// </summary>
    /// <remarks>
    /// Once, and from the first answer the service gave. The tray opens its
    /// window as it starts, and the state that decides the tab is settled by
    /// then -- but a window that re-decided on every poll would move a
    /// technician off the tab they had just opened, five seconds after they
    /// opened it, and would do it again every time a cycle finished. So this
    /// is a starting point rather than a rule, which is also what makes it
    /// safe: getting it wrong costs one click.
    /// </remarks>
    private void ChooseTab()
    {
        if (_tabChosen || _status is null || !_serviceReached)
        {
            return;
        }

        _tabChosen = true;

        SelectedTab = TrayTabs.For(NextStep);
    }

    /// <summary>
    /// ADL's liveness states, in the words the window says them in.
    /// </summary>
    /// <remarks>
    /// Kept beside nothing: this is a copy of a vocabulary ADL owns, and it
    /// is a copy on purpose -- see <see cref="FleetStatus"/>. A state added
    /// in ADL and not here falls through to <see cref="Humanised"/> rather
    /// than to the identifier.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> FleetStates =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["online"] = "Collecting and sending",
            ["degraded"] = "Heartbeats are late",
            ["offline"] = "No heartbeats arriving",
            ["cycle_stuck"] = "Alive but not scanning",
            ["unknown"] = "Nothing reported yet",
        };

    /// <summary>
    /// The same states as <see cref="FleetStates"/>, as the colour the header
    /// draws beside them.
    /// </summary>
    /// <remarks>
    /// A second dictionary rather than a second column of the first, because
    /// the two are gated differently: the words show on any machine ADL has
    /// ever spoken about, and the dot only on one that is paired right now.
    /// Anything missing here is grey by <see cref="FleetTone"/>, which is
    /// what a state this build has never heard of should be.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, TrayState> FleetTones =
        new Dictionary<string, TrayState>(StringComparer.OrdinalIgnoreCase)
        {
            ["online"] = TrayState.Working,
            ["degraded"] = TrayState.NeedsAttention,
            ["offline"] = TrayState.Stopped,
            ["cycle_stuck"] = TrayState.NeedsAttention,
            ["unknown"] = TrayState.Unknown,
        };

    /// <summary>
    /// Everything derived from the status answer.
    /// </summary>
    /// <remarks>
    /// Listed once and raised together, because they change together: they
    /// are all views of one snapshot, and raising them one at a time as each
    /// was noticed is how a header comes to show a device name beside
    /// "Unpaired".
    /// </remarks>
    private static readonly IReadOnlyList<string> HeaderProperties =
    [
        nameof(ServiceRunning), nameof(AdlUrl), nameof(AdlLink), nameof(IsConfigured), nameof(NeedsConfiguring),
        nameof(ConfigurationHint), nameof(ShowsChangeAdl),
        nameof(AgentVersion), nameof(DeviceName), nameof(DeviceId),
        nameof(PairingLine), nameof(IsPaired), nameof(NeedsRePairing), nameof(HasEverPaired),
        nameof(PairedSince), nameof(ShowsPairingBox), nameof(ShowsPairAgain),
        nameof(ShowsCancelPairing),
        nameof(FleetStatus), nameof(FleetTone),
        nameof(LastHeartbeat), nameof(LastSynced), nameof(ConfigVersion), nameof(AdlVersion),
        nameof(CheckInterval),
        nameof(LastHeartbeatAgo), nameof(LastSyncedAgo),
        nameof(ClockSkew), nameof(Reconciles), nameof(LastError), nameof(UpdateStatus),
        nameof(Collecting), nameof(CollectingNow),
        nameof(ShowsAdlFacts), nameof(ShowsPairedTo), nameof(ShowsHeadline),
        nameof(Headline), nameof(HasNoConnections), nameof(ShowsConnectionHint),
        nameof(ShowsMachineReason), nameof(ShowsConnectionReason),
        nameof(StationsAvailable),
    ];
}

/// <summary>
/// What the agent had to say about a station's recent passes.
/// </summary>
/// <remarks>
/// <paramref name="More"/> rather than a silent truncation: a window showing
/// six of a station's passes without saying there are more reads as a machine
/// that has only run six times, which is the exact misreading this whole
/// record exists to stop.
/// </remarks>
public sealed record PassesAnswer(
    IReadOnlyList<PassRowViewModel> Passes, bool More, string? Problem);
