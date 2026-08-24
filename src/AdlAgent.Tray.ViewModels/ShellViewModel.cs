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

    /// <summary>The station list the rows were last built from, as it arrived.</summary>
    private string _shownStations = "";

    public ShellViewModel(AgentControlLink agent)
    {
        _agent = agent;

        PairCommand = new AsyncCommand(PairAsync, Failed, () => PairingCode.Trim().Length > 0);
        RefreshCommand = new AsyncCommand(() => RefreshAsync(), Failed);
        SaveStationCommand = new AsyncCommand(
            SaveStationAsync, Failed, () => SelectedStation?.HasChanges == true);
    }

    public ObservableCollection<StationViewModel> Stations { get; } = [];

    public AsyncCommand PairCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand SaveStationCommand { get; }

    /// <summary>Raised when a station's boxes change, so the window can re-count.</summary>
    public event EventHandler? StationSettingsChanged;

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
    /// The one line the tray icon's tooltip and the window header both show.
    /// </summary>
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
                return "ADL has revoked this machine. Ask for a new pairing code and pair again.";
            }

            if (!IsPaired)
            {
                return "Not paired yet. Paste the pairing code from your ADL administrator.";
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

    /// <summary>True when there are no station rows to draw.</summary>
    public bool HasNoStations => Stations.Count == 0;

    /// <summary>
    /// Why the station list is empty, in the words of the reason it is.
    /// </summary>
    /// <remarks>
    /// "ADL has linked nothing to this device yet", "ADL is not answering" and
    /// "the service is not running" are three different problems wanting three
    /// different people, and until this they were the same empty grid.
    /// </remarks>
    public string NoStationsReason => NextStep.NoStations;

    /// <summary>The answer to the last thing a button did.</summary>
    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
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

    public StationViewModel? SelectedStation
    {
        get => _selectedStation;
        set
        {
            if (ReferenceEquals(_selectedStation, value))
            {
                return;
            }

            if (_selectedStation is not null)
            {
                _selectedStation.SettingsChanged -= StationEdited;
            }

            Set(ref _selectedStation, value);

            if (_selectedStation is not null)
            {
                _selectedStation.SettingsChanged += StationEdited;
            }

            Raise(nameof(HasSelectedStation));
            SaveStationCommand.Refresh();

            // The new station's boxes have not been counted against the
            // filesystem yet, and the technician is looking at them now.
            StationSettingsChanged?.Invoke(this, EventArgs.Empty);
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
    /// <param name="replaceEvenIfEdited">
    /// Set by the refresh that follows a save, where boxes differing from
    /// what ADL sent is exactly what has just been fixed. A parameter and not
    /// a field: the poll can tick during this method's awaits, and a field
    /// would let that tick inherit the permission and overwrite whatever
    /// somebody had started typing in the meantime.
    /// </param>
    public async Task RefreshAsync(bool replaceEvenIfEdited = false)
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

                Show(stations.Value, replaceEvenIfEdited);
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

    /// <summary>
    /// Count what the selected station's boxes would match (story 7).
    /// </summary>
    /// <remarks>
    /// Called from a debounce in the window rather than on every keystroke:
    /// each call walks a folder that may hold a hundred thousand files.
    /// </remarks>
    public async Task CountMatchesAsync()
    {
        var station = SelectedStation;

        if (station is null)
        {
            return;
        }

        var counted = await _agent.PreviewAsync(station.PreviewRequest()).ConfigureAwait(true);

        // The selection may have moved while that was travelling. Answering
        // the station that is no longer shown would put one station's count
        // under another's name.
        if (!ReferenceEquals(station, SelectedStation))
        {
            return;
        }

        if (counted.Value is null)
        {
            station.CouldNotCount(counted.Detail ?? "The agent could not count these settings.");

            return;
        }

        station.Counted(counted.Value);
    }

    /// <summary>Write the selected station's changed settings through to ADL (story 9).</summary>
    public async Task SaveStationAsync()
    {
        var station = SelectedStation;

        if (station is null)
        {
            return;
        }

        var changes = station.Changes();

        if (changes.Count == 0)
        {
            Message = "Nothing has changed.";

            return;
        }

        var written = await _agent.ConfigureAsync(station.StationLinkId, changes).ConfigureAwait(true);

        if (!written.Ok)
        {
            Message = written.Detail ?? "ADL would not accept those settings.";

            if (written.NeedsRePairing)
            {
                // Read back rather than asserted here. A revoked token is a
                // fact about the machine that the service already holds, and
                // the line at the top of the window is drawn from that -- so
                // this asks now instead of leaving it wrong until the poll
                // comes round. Editing is safe: the refresh keeps rows that
                // somebody is typing into.
                await RefreshAsync().ConfigureAwait(true);
            }

            return;
        }

        Message = string.Create(
            CultureInfo.CurrentCulture,
            $"Saved to ADL. Configuration is now at version {written.Value!.ConfigVersion}.");

        // Re-read rather than patch the row in place: what this window shows
        // should be what ADL holds, including anything it normalised on the
        // way in. This is the refresh that is allowed to replace edited
        // boxes, because the edit in them is the one that just landed.
        await RefreshAsync(replaceEvenIfEdited: true).ConfigureAwait(true);
    }

    /// <summary>
    /// Replace the rows with what the service just said, keeping the
    /// selection -- but only when the stations themselves have changed and
    /// nobody is in the middle of typing.
    /// </summary>
    /// <remarks>
    /// Both guards are about the poll rather than about pressing Refresh.
    /// This runs every few seconds for as long as the window is open, and
    /// rebuilding the rows each time would take the keyboard focus away from
    /// whoever was typing a folder path -- and, worse, would replace what they
    /// had typed with what ADL still holds. A technician cannot type a path
    /// into a box that empties itself every five seconds.
    /// <para>
    /// The comparison is against the stations alone and not the whole answer,
    /// which also carries the moment of the last sync and the last cycle.
    /// Those move on every cycle without any station having changed, and
    /// rebuilding on them would throw away the live match count a technician
    /// was reading -- occasionally, and for no reason they could see.
    /// </para>
    /// <para>
    /// What it does compare is the stations as they arrived, rather than a
    /// hand-listed set of fields, so a field added to the station snapshot is
    /// noticed here without anybody remembering to add it.
    /// </para>
    /// </remarks>
    private void Show(AgentStationsSnapshot stations, bool replaceEvenIfEdited)
    {
        if (!replaceEvenIfEdited && SelectedStation?.HasChanges == true)
        {
            return;
        }

        var arrived = JsonSerializer.Serialize(stations.Stations, AgentJson.Options);

        if (!replaceEvenIfEdited && arrived == _shownStations)
        {
            return;
        }

        _shownStations = arrived;

        // Carried across the rebuild. The count belongs to the folder and the
        // pattern, neither of which this is changing, and blanking it would
        // have the sentence a technician is reading disappear the moment a
        // cycle finished somewhere behind them.
        var selected = SelectedStation?.StationLinkId;
        var counted = SelectedStation?.MatchSummary;

        SelectedStation = null;
        Stations.Clear();

        foreach (var station in stations.Stations)
        {
            Stations.Add(new StationViewModel(station));
        }

        var showing = Stations.FirstOrDefault(station => station.StationLinkId == selected);

        if (showing is not null && counted is { Length: > 0 })
        {
            showing.KeepCount(counted);
        }

        SelectedStation = showing ?? Stations.FirstOrDefault();
    }

    private void StationEdited(object? sender, EventArgs args)
    {
        SaveStationCommand.Refresh();
        StationSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Failed(Exception exception) =>
        Message = $"Something went wrong in this window: {exception.Message}";

    /// <summary>
    /// Re-read everything the window draws from the last answer: the header,
    /// the next-step line, and -- once -- which tab to be on.
    /// </summary>
    private void Restate()
    {
        NextStep = NextSteps.For(_serviceReached, _status, _linked);

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

        SelectedTab = TrayTabs.For(_status);
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
        nameof(Headline), nameof(HasNoStations),
    ];
}
