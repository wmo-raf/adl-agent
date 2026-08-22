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
    private string _alert = "";
    private StationViewModel? _selectedStation;

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

    public string AdlUrl => _status?.AdlUrl ?? "-";

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
    /// The banner: something a technician should act on, or nothing.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same as <see cref="Headline"/>. A working
    /// machine has a headline and no banner, and a banner that was always
    /// there would be a banner nobody reads.
    /// </remarks>
    public string Alert
    {
        get => _alert;
        private set
        {
            if (Set(ref _alert, value))
            {
                Raise(nameof(HasAlert));
            }
        }
    }

    public bool HasAlert => Alert.Length > 0;

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

        Alert = status.ServiceReached ? AlertFor(_status) : Headline;

        RaiseHeader();

        if (!status.ServiceReached)
        {
            return;
        }

        var stations = await _agent.StationsAsync().ConfigureAwait(true);

        if (stations.Value is null)
        {
            return;
        }

        Show(stations.Value, replaceEvenIfEdited);
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

        RaiseHeader();

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
                Alert = "ADL has revoked this machine. Ask for a new pairing code and pair again.";
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

    /// <summary>
    /// What is worth a banner, in the order a technician should act on it.
    /// </summary>
    private static string AlertFor(AgentStatusSnapshot? status)
    {
        if (status is null)
        {
            return "";
        }

        if (status.RePairNeeded)
        {
            return "ADL has revoked this machine's token. Ask your ADL administrator for a new "
                + "pairing code and pair again — nothing is being sent until you do.";
        }

        if (status.PairingState == nameof(CorePairingState.Unpaired))
        {
            return "This machine is not paired with ADL yet.";
        }

        if (status.ConfigFromCache)
        {
            return "ADL could not be reached, so the agent is working from the settings it last "
                + "received. Files already found are still being collected and will be offered "
                + "when the link returns.";
        }

        return "";
    }

    private void Failed(Exception exception) =>
        Message = $"Something went wrong in this window: {exception.Message}";

    private void RaiseHeader()
    {
        foreach (var property in HeaderProperties)
        {
            Raise(property);
        }
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
        nameof(ServiceRunning), nameof(AdlUrl), nameof(AgentVersion), nameof(DeviceName), nameof(DeviceId),
        nameof(PairingState), nameof(IsPaired), nameof(NeedsRePairing), nameof(FleetStatus),
        nameof(LastHeartbeat), nameof(LastSynced), nameof(ConfigVersion), nameof(CheckInterval),
        nameof(ClockSkew), nameof(PairedAt), nameof(LastError), nameof(UpdateStatus),
        nameof(Headline),
    ];
}
