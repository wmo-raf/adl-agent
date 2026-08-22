using System;
using System.Globalization;
using System.Text.Json.Nodes;
using AdlAgent.Core.Api;
using AdlAgent.Core.Cycle;
using AdlAgent.Core.Status;

namespace AdlAgent.Tray;

/// <summary>
/// One station in the list, and the settings box beneath it.
/// </summary>
/// <remarks>
/// Both halves are here because they are one thing to the technician: the row
/// they clicked and the folder they are typing into. Splitting them would
/// mean keeping a selection and an editor in step, which is the bug this
/// window is most likely to have.
/// <para>
/// <see cref="Changes"/> is the whole of what "editing" means here: the
/// fields whose boxes now differ from what ADL sent. Nothing is stored, and
/// nothing is applied locally -- the window holds a difference until somebody
/// presses Save, and then ADL holds it.
/// </para>
/// </remarks>
public sealed class StationViewModel : Observable
{
    private readonly AgentStationSnapshot _station;

    private string _localFolderPath;
    private string _filePattern;
    private string _listingStrategy;
    private string _directFetchPrefix;
    private string _directFetchIntervalMinutes;
    private string _directFetchDatetimeFormat;
    private string _directFetchDatetimeTimezone;
    private string _directFetchFileExtension;
    private string _stabilityWindowSeconds;
    private string _matchSummary = "";

    public StationViewModel(AgentStationSnapshot station)
    {
        _station = station;

        var config = station.Config;

        _localFolderPath = config.LocalFolderPath;
        _filePattern = config.FilePattern ?? "";
        _listingStrategy = ListingStrategies.IsDirectFetch(config.ListingStrategy)
            ? ListingStrategies.DirectFetch
            : ListingStrategies.Enumerate;
        _directFetchPrefix = config.DirectFetchPrefix ?? "";
        _directFetchIntervalMinutes = Number(config.DirectFetchIntervalMinutes);
        _directFetchDatetimeFormat = config.DirectFetchDatetimeFormat ?? "";
        _directFetchDatetimeTimezone = config.DirectFetchDatetimeTimezone ?? "";
        _directFetchFileExtension = config.DirectFetchFileExtension ?? "";
        _stabilityWindowSeconds = Number(config.StabilityWindowSeconds);
    }

    public long StationLinkId => _station.StationLinkId;

    public string StationName => _station.StationName;

    public string StationId => _station.StationId;

    public string ConnectionName => _station.ConnectionName;

    public string Timezone => _station.Timezone;

    public bool Enabled => _station.Enabled;

    /// <summary>HQ's collection start date, shown and never editable here.</summary>
    public string StartDate => Moment(_station.StartDate);

    public string Watermark => Moment(_station.Watermark);

    /// <summary>What the last cycle did for this station, in one cell.</summary>
    public string LastCycle => _station.Scanned is null
        ? "no cycle yet"
        : string.Create(
            CultureInfo.CurrentCulture,
            $"{_station.Scanned} seen, {_station.Uploaded} sent, {_station.Failed} failed");

    /// <summary>What went wrong for this station last cycle, if anything did.</summary>
    public string Error => _station.Error ?? "";

    public bool HasError => !string.IsNullOrEmpty(_station.Error);

    // ---------- the tier this window may write ----------

    public string LocalFolderPath
    {
        get => _localFolderPath;
        set { if (Set(ref _localFolderPath, value)) { Edited(); } }
    }

    public string FilePattern
    {
        get => _filePattern;
        set { if (Set(ref _filePattern, value)) { Edited(); } }
    }

    public string ListingStrategy
    {
        get => _listingStrategy;
        set
        {
            if (Set(ref _listingStrategy, value))
            {
                Raise(nameof(IsDirectFetch));
                Edited();
            }
        }
    }

    /// <summary>True when the settings below the strategy box are the direct-fetch ones.</summary>
    public bool IsDirectFetch => ListingStrategies.IsDirectFetch(_listingStrategy);

    public string DirectFetchPrefix
    {
        get => _directFetchPrefix;
        set { if (Set(ref _directFetchPrefix, value)) { Edited(); } }
    }

    public string DirectFetchIntervalMinutes
    {
        get => _directFetchIntervalMinutes;
        set { if (Set(ref _directFetchIntervalMinutes, value)) { Edited(); } }
    }

    public string DirectFetchDatetimeFormat
    {
        get => _directFetchDatetimeFormat;
        set { if (Set(ref _directFetchDatetimeFormat, value)) { Edited(); } }
    }

    public string DirectFetchDatetimeTimezone
    {
        get => _directFetchDatetimeTimezone;
        set { if (Set(ref _directFetchDatetimeTimezone, value)) { Edited(); } }
    }

    public string DirectFetchFileExtension
    {
        get => _directFetchFileExtension;
        set { if (Set(ref _directFetchFileExtension, value)) { Edited(); } }
    }

    public string StabilityWindowSeconds
    {
        get => _stabilityWindowSeconds;
        set { if (Set(ref _stabilityWindowSeconds, value)) { Edited(); } }
    }

    /// <summary>The live count, as a sentence: story 7's whole visible half.</summary>
    public string MatchSummary
    {
        get => _matchSummary;
        private set => Set(ref _matchSummary, value);
    }

    /// <summary>Raised whenever a box changes, so the window can re-count.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>True when a box differs from what ADL sent.</summary>
    public bool HasChanges => Changes().Count > 0;

    /// <summary>
    /// What to send: the fields that differ, and only those.
    /// </summary>
    /// <remarks>
    /// Only those, because a write that named every field would assert a
    /// value for the ones a newer ADL knows about and this tray does not --
    /// silently resetting them every time somebody fixed a typo in a pattern.
    /// It is also what lets ADL name the field a person actually typed when
    /// it refuses one.
    /// </remarks>
    public JsonObject Changes()
    {
        var changes = new JsonObject();
        var stored = _station.Config;

        Differs(changes, "local_folder_path", stored.LocalFolderPath, LocalFolderPath);
        Differs(changes, "file_pattern", stored.FilePattern ?? "", FilePattern);
        Differs(changes, "listing_strategy", Strategy(stored.ListingStrategy), ListingStrategy);
        Differs(changes, "direct_fetch_prefix", stored.DirectFetchPrefix ?? "", DirectFetchPrefix);
        Differs(changes, "direct_fetch_datetime_format", stored.DirectFetchDatetimeFormat ?? "", DirectFetchDatetimeFormat);
        Differs(changes, "direct_fetch_datetime_timezone", stored.DirectFetchDatetimeTimezone ?? "", DirectFetchDatetimeTimezone);
        Differs(changes, "direct_fetch_file_extension", stored.DirectFetchFileExtension ?? "", DirectFetchFileExtension);

        Whole(changes, "direct_fetch_interval_minutes", stored.DirectFetchIntervalMinutes, DirectFetchIntervalMinutes);
        Whole(changes, "stability_window_seconds", stored.StabilityWindowSeconds, StabilityWindowSeconds);

        return changes;
    }

    /// <summary>
    /// The settings to count against the filesystem: the station, and the
    /// boxes as they now read.
    /// </summary>
    public JsonObject PreviewRequest()
    {
        var request = Changes();

        request["station_link_id"] = StationLinkId;

        return request;
    }

    /// <summary>Say what the agent counted.</summary>
    public void Counted(FolderPreviewResult preview)
    {
        if (preview.Problem is not null)
        {
            MatchSummary = preview.Problem;

            return;
        }

        var files = preview.Matches == 1 ? "file" : "files";
        var qualifier = preview.Truncated ? "at least " : "";

        MatchSummary = preview.Sample.Count == 0
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"{qualifier}{preview.Matches} {files} match in this folder.")
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{qualifier}{preview.Matches} {files} match in this folder, newest {string.Join(", ", preview.Sample)}.");
    }

    /// <summary>Say why nothing could be counted.</summary>
    public void CouldNotCount(string detail) => MatchSummary = detail;

    private void Edited()
    {
        Raise(nameof(HasChanges));
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Differs(JsonObject changes, string field, string stored, string typed)
    {
        if (!string.Equals(stored, typed, StringComparison.Ordinal))
        {
            changes[field] = typed;
        }
    }

    /// <summary>
    /// A number the technician typed, sent only when it is both different and
    /// a number.
    /// </summary>
    /// <remarks>
    /// A half-typed box is not an edit. Somebody clearing the interval field
    /// to retype it passes through "" and "1" on the way to "10", and neither
    /// is worth sending to ADL -- nor worth refusing them for.
    /// </remarks>
    private static void Whole(JsonObject changes, string field, int? stored, string typed)
    {
        if (!int.TryParse(typed, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value))
        {
            return;
        }

        if (value != stored)
        {
            changes[field] = value;
        }
    }

    private static string Strategy(string? stored) =>
        ListingStrategies.IsDirectFetch(stored) ? ListingStrategies.DirectFetch : ListingStrategies.Enumerate;

    private static string Number(int? value) =>
        value is null ? "" : value.Value.ToString(CultureInfo.CurrentCulture);

    private static string Moment(DateTimeOffset? value) =>
        value is null ? "-" : value.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
