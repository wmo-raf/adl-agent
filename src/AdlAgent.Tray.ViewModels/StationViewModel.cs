using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdlAgent.Core.Api;
using AdlAgent.Core.Cycle;
using AdlAgent.Core.Serialization;
using AdlAgent.Core.Status;

namespace AdlAgent.Tray;

/// <summary>
/// One station: either a row in the list, or the boxes in the settings window
/// opened from it.
/// </summary>
/// <remarks>
/// One class for both, but never one <em>instance</em> for both. A row is
/// built from what ADL sent and is never typed into; opening the settings
/// window calls <see cref="Editing"/>, which builds a second instance from
/// the same snapshot for somebody to type into. That is what makes Cancel
/// correct without a revert method to write -- the copy is simply dropped --
/// and it is why the poll behind the window can never replace the object
/// being edited.
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

    /// <summary>
    /// What to say about a folder path, on the instance that has boxes.
    /// </summary>
    /// <remarks>
    /// Null on a row, because a row has nowhere to say it. Rather than making
    /// every caller supply one, an absent chooser simply means every path is
    /// unremarkable.
    /// </remarks>
    private readonly FolderChoice? _folders;

    private string _localFolderPath;
    private string _localFolderNote;
    private string _filePattern;
    private bool _dirStructuredByDate;
    private string _dateGranularity;
    private string _monthDirFormat;
    private string _listingStrategy;
    private string _directFetchPrefix;
    private string _directFetchIntervalMinutes;
    private string _directFetchDatetimeFormat;
    private string _directFetchDatetimeTimezone;
    private string _directFetchFileExtension;
    private string _stabilityWindowSeconds;
    private string _matchSummary = "";

    public StationViewModel(AgentStationSnapshot station, FolderChoice? folders = null)
    {
        _station = station;
        _folders = folders;

        var config = station.Config;

        _localFolderPath = config.LocalFolderPath;
        _localFolderNote = folders?.NoteFor(config.LocalFolderPath) ?? "";
        _filePattern = config.FilePattern ?? "";
        _dirStructuredByDate = config.DirStructuredByDate;
        _dateGranularity = config.DateGranularity ?? "";
        _monthDirFormat = config.MonthDirFormat ?? "";
        _listingStrategy = Strategy(config.ListingStrategy);
        _directFetchPrefix = config.DirectFetchPrefix ?? "";
        _directFetchIntervalMinutes = Number(config.DirectFetchIntervalMinutes);
        _directFetchDatetimeFormat = config.DirectFetchDatetimeFormat ?? "";
        _directFetchDatetimeTimezone = config.DirectFetchDatetimeTimezone ?? "";
        _directFetchFileExtension = config.DirectFetchFileExtension ?? "";
        _stabilityWindowSeconds = Number(config.StabilityWindowSeconds);
    }

    /// <summary>Raised whenever a box changes, so the window can re-count.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>
    /// A second instance of this station for somebody to type into.
    /// </summary>
    /// <remarks>
    /// Built from the snapshot rather than from this object's boxes, so what
    /// the settings window opens showing is what ADL holds -- which is also
    /// what the row behind it goes on showing while the window is open.
    /// Nothing binds the two together, and nothing has to.
    /// </remarks>
    public StationViewModel Editing(FolderChoice folders) => new(_station, folders);

    public long StationLinkId => _station.StationLinkId;

    public string StationName => _station.StationName;

    public string StationId => _station.StationId;

    public string ConnectionName => _station.ConnectionName;

    public string Timezone => _station.Timezone;

    public bool Enabled => _station.Enabled;

    /// <summary>HQ's collection start date, shown and never editable here.</summary>
    public string StartDate => Display.Moment(_station.StartDate);

    public string Watermark => Display.Moment(_station.Watermark);

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

        set
        {
            if (Set(ref _localFolderPath, value))
            {
                Note();
                Edited();
            }
        }
    }

    /// <summary>
    /// What is worth saying about this folder path beyond whether files match
    /// it, or nothing when there is nothing.
    /// </summary>
    /// <remarks>
    /// In practice this is about the gap between who is looking and who will
    /// be reading: the technician browses as themselves, and the service
    /// collects as LocalSystem. See <see cref="FolderChoice.NoteFor"/>.
    /// </remarks>
    public string LocalFolderNote => _localFolderNote;

    public bool HasLocalFolderNote => _localFolderNote.Length > 0;

    public string FilePattern
    {
        get => _filePattern;

        set
        {
            if (Set(ref _filePattern, value))
            {
                Edited();
            }
        }
    }

    /// <summary>
    /// True when this station's files sit under dated sub-folders.
    /// </summary>
    /// <remarks>
    /// Editable even though this version of the agent does not walk such
    /// folders. It is a setting ADL holds and the tier this window owns, and
    /// a station an administrator left it switched on for would otherwise be
    /// one a technician is told about ("this version does not walk dated
    /// sub-folders") and cannot do anything about.
    /// </remarks>
    public bool DirStructuredByDate
    {
        get => _dirStructuredByDate;

        set
        {
            if (Set(ref _dirStructuredByDate, value))
            {
                Edited();
            }
        }
    }

    public string DateGranularity
    {
        get => _dateGranularity;

        set
        {
            if (Set(ref _dateGranularity, value))
            {
                Edited();
            }
        }
    }

    public string MonthDirFormat
    {
        get => _monthDirFormat;

        set
        {
            if (Set(ref _monthDirFormat, value))
            {
                Edited();
            }
        }
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

        set
        {
            if (Set(ref _directFetchPrefix, value))
            {
                Edited();
            }
        }
    }

    public string DirectFetchIntervalMinutes
    {
        get => _directFetchIntervalMinutes;

        set
        {
            if (Set(ref _directFetchIntervalMinutes, value))
            {
                Edited();
            }
        }
    }

    public string DirectFetchDatetimeFormat
    {
        get => _directFetchDatetimeFormat;

        set
        {
            if (Set(ref _directFetchDatetimeFormat, value))
            {
                Edited();
            }
        }
    }

    public string DirectFetchDatetimeTimezone
    {
        get => _directFetchDatetimeTimezone;

        set
        {
            if (Set(ref _directFetchDatetimeTimezone, value))
            {
                Edited();
            }
        }
    }

    public string DirectFetchFileExtension
    {
        get => _directFetchFileExtension;

        set
        {
            if (Set(ref _directFetchFileExtension, value))
            {
                Edited();
            }
        }
    }

    public string StabilityWindowSeconds
    {
        get => _stabilityWindowSeconds;

        set
        {
            if (Set(ref _stabilityWindowSeconds, value))
            {
                Edited();
            }
        }
    }

    /// <summary>The live count, as a sentence: story 7's whole visible half.</summary>
    public string MatchSummary
    {
        get => _matchSummary;

        private set => Set(ref _matchSummary, value);
    }

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
    /// <para>
    /// Compared as JSON rather than field by field. A hand-written comparison
    /// has to be extended whenever the tier grows, and the failure when it is
    /// not is silent: the box appears, the technician types in it, Save
    /// reports success, and the setting never leaves the machine. Here, a
    /// field this window does not render simply never differs from what ADL
    /// sent, and one it does render cannot be forgotten.
    /// </para>
    /// </remarks>
    public JsonObject Changes()
    {
        var stored = Wire(_station.Config);
        var typed = Wire(Typed());
        var changes = new JsonObject();

        foreach (var field in typed)
        {
            if (!JsonNode.DeepEquals(field.Value, stored[field.Key]))
            {
                changes[field.Key] = field.Value?.DeepClone();
            }
        }

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

    /// <summary>
    /// What ADL sent, with the boxes this window renders laid over it.
    /// </summary>
    /// <remarks>
    /// Built from the stored configuration rather than from nothing, so that
    /// a setting this version of the tray has no box for keeps the value ADL
    /// holds and therefore never appears as a change.
    /// </remarks>
    private StationLinkAppConfig Typed()
    {
        var stored = _station.Config;

        return stored with
        {
            LocalFolderPath = LocalFolderPath,
            FilePattern = Same(FilePattern, stored.FilePattern),
            DirStructuredByDate = DirStructuredByDate,
            DateGranularity = Same(DateGranularity, stored.DateGranularity),
            MonthDirFormat = Same(MonthDirFormat, stored.MonthDirFormat),
            ListingStrategy = ListingStrategy,
            DirectFetchPrefix = Same(DirectFetchPrefix, stored.DirectFetchPrefix),
            DirectFetchDatetimeFormat = Same(DirectFetchDatetimeFormat, stored.DirectFetchDatetimeFormat),
            DirectFetchDatetimeTimezone = Same(DirectFetchDatetimeTimezone, stored.DirectFetchDatetimeTimezone),
            DirectFetchFileExtension = Same(DirectFetchFileExtension, stored.DirectFetchFileExtension),
            DirectFetchIntervalMinutes = Whole(DirectFetchIntervalMinutes) ?? stored.DirectFetchIntervalMinutes,
            StabilityWindowSeconds = Whole(StabilityWindowSeconds) ?? stored.StabilityWindowSeconds,
        };
    }

    /// <summary>
    /// An empty box, read back as whatever emptiness ADL sent.
    /// </summary>
    /// <remarks>
    /// ADL distinguishes a setting that is blank from one that is not set --
    /// several of these columns are <c>null=True</c>, and the sync response
    /// carries a genuine <c>null</c> for them. A text box cannot hold that
    /// difference: it shows an empty string either way. Without this, every
    /// station with an unset prefix would look edited the moment its row was
    /// drawn, the Save button would come up on a window nobody had touched,
    /// and the list would stop refreshing because something appeared to be
    /// in the middle of being typed.
    /// <para>
    /// So an empty box means "unchanged" when the setting was unset, and
    /// means "cleared" when it held something.
    /// </para>
    /// </remarks>
    private static string? Same(string typed, string? stored) =>
        typed.Length == 0 && stored is null ? null : typed;

    private void Edited()
    {
        Raise(nameof(HasChanges));
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Note()
    {
        if (Set(ref _localFolderNote, _folders?.NoteFor(_localFolderPath) ?? "", nameof(LocalFolderNote)))
        {
            Raise(nameof(HasLocalFolderNote));
        }
    }

    /// <summary>
    /// A number the technician typed, or <c>null</c> if the box is not one
    /// yet.
    /// </summary>
    /// <remarks>
    /// A half-typed box is not an edit. Somebody clearing the interval field
    /// to retype it passes through "" and "1" on the way to "10", and neither
    /// is worth sending to ADL -- nor worth refusing them for.
    /// </remarks>
    private static int? Whole(string typed) =>
        int.TryParse(typed, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)
            ? value
            : null;

    private static JsonObject Wire(StationLinkAppConfig config) =>
        JsonSerializer.SerializeToNode(config, AgentJson.Options)!.AsObject();

    private static string Strategy(string? stored) =>
        ListingStrategies.IsDirectFetch(stored) ? ListingStrategies.DirectFetch : ListingStrategies.Enumerate;

    private static string Number(int? value) =>
        value is null ? "" : value.Value.ToString(CultureInfo.CurrentCulture);
}
