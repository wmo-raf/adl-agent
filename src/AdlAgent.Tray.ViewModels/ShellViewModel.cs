using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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

    private AgentStatusSnapshot? _status;
    private bool _serviceReached;
    private string _pairingCode = "";
    private string _message = "";
    private ConnectionViewModel? _selectedConnection;
    private StationViewModel? _selectedStation;
    private NextStep _nextStep = NextSteps.Unknown;
    private int _selectedTab = TrayTabs.Pairing;

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

    public ShellViewModel(AgentControlLink agent)
    {
        _agent = agent;

        PairCommand = new AsyncCommand(PairAsync, Failed, () => PairingCode.Trim().Length > 0);
        RefreshCommand = new AsyncCommand(() => RefreshAsync(), Failed);
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

    public AsyncCommand RefreshCommand { get; }

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

    /// <summary>True when this machine has an address to send to.</summary>
    public bool IsConfigured => _status is null || _status.Configured;

    /// <summary>The tier-appropriate next step, when there is one to take.</summary>
    public string ConfigurationHint => _status?.ConfigurationHint ?? "";

    /// <summary>
    /// True when the window should be showing somebody how to give this
    /// machine an address.
    /// </summary>
    public bool NeedsConfiguring => !IsConfigured;

    public string AgentVersion => _status?.AgentVersion ?? "-";

    public string DeviceName => _status?.DeviceName ?? "-";

    public string DeviceId => _status?.DeviceId?.ToString(CultureInfo.CurrentCulture) ?? "-";

    public string PairingState => _status?.PairingState ?? "Unknown";

    public bool IsPaired => _status?.PairingState == nameof(CorePairingState.Paired);

    public bool NeedsRePairing => _status?.RePairNeeded == true;

    public string FleetStatus => _status?.FleetStatus ?? "-";

    public string LastHeartbeat => Display.Moment(_status?.LastHeartbeatAt);

    public string LastSynced => Display.Moment(_status?.LastSyncedAt);

    public string ConfigVersion => _status?.ConfigVersion?.ToString(CultureInfo.CurrentCulture) ?? "-";

    public string CheckInterval => _status is null
        ? "-"
        : string.Create(CultureInfo.CurrentCulture, $"every {_status.CheckIntervalMinutes} minutes");

    public string ClockSkew => _status?.ClockSkewSeconds is null
        ? "-"
        : string.Create(CultureInfo.CurrentCulture, $"{_status.ClockSkewSeconds} seconds");

    public string PairedAt => Display.Moment(_status?.PairedAt);

    public string LastError => _status?.LastError ?? "";

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
    /// The one line the tray icon's tooltip and the window header both show:
    /// what this machine <em>is</em>.
    /// </summary>
    /// <remarks>
    /// Only what it is. What to do about it is <see cref="NextStep"/>, which
    /// is on the screen directly beneath this, and a header that also gave
    /// the instruction would be the same sentence twice -- and, worse, two
    /// copies of it to drift apart. This one describes; the one below acts.
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

            return string.Create(
                CultureInfo.CurrentCulture,
                $"Paired to {AdlUrl} as {DeviceName} — ADL says {FleetStatus}.");
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
    /// on an empty grid selection with "Edit settings…" greyed out, and a
    /// technician has to click twice to do anything -- which is the same
    /// reason <see cref="Show"/> falls back to the first station when it
    /// rebuilds.
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

        if (status.ServiceReached)
        {
            var stations = await _agent.StationsAsync().ConfigureAwait(true);

            if (stations.Value is not null)
            {
                _linked = stations.Value.Stations;

                Show(stations.Value);
            }
        }

        // Last, and on every path through the method above -- including the
        // ones that changed no row. This is the poll, and the line at the top
        // of the window is what tells a technician that waiting was the right
        // thing to be doing. It has to move on its own or it is not one.
        Restate();
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
        Message = string.Create(
            CultureInfo.CurrentCulture,
            $"Paired. ADL knows this machine as {paired.Value!.DeviceName}.");

        _status = paired.Value;
        _serviceReached = true;

        Restate();

        await RefreshAsync().ConfigureAwait(true);
    }

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

            Connections.Add(new ConnectionViewModel(each, owned ?? []));
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
        nameof(ServiceRunning), nameof(AdlUrl), nameof(IsConfigured), nameof(NeedsConfiguring),
        nameof(ConfigurationHint),
        nameof(AgentVersion), nameof(DeviceName), nameof(DeviceId),
        nameof(PairingState), nameof(IsPaired), nameof(NeedsRePairing), nameof(FleetStatus),
        nameof(LastHeartbeat), nameof(LastSynced), nameof(ConfigVersion), nameof(CheckInterval),
        nameof(ClockSkew), nameof(PairedAt), nameof(LastError), nameof(UpdateStatus),
        nameof(Headline), nameof(HasNoConnections), nameof(ShowsConnectionHint),
        nameof(ShowsMachineReason), nameof(ShowsConnectionReason),
    ];
}
