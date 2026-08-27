using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AdlAgent.Core.Diagnostics;

namespace AdlAgent.Tray;

/// <summary>
/// What one pass did, opened out: the stations in it and the files that did
/// something.
/// </summary>
/// <remarks>
/// Tables rather than the text form, because that is what opening a row in a
/// table should give somebody -- columns that line up and can be read across.
/// The text form is still here, on <see cref="Text"/>, and it is what Copy
/// puts on the clipboard: the pass a technician pastes into an email and the
/// pass in the diagnostics bundle are then the same sentences, which is where
/// having one renderer actually mattered.
/// </remarks>
public sealed class PassDetailViewModel : Observable
{
    private readonly CycleRecord _record;

    public PassDetailViewModel(CycleRecord record)
    {
        _record = record;

        Stations = record.Stations.Select(station => new PassStationViewModel(station)).ToList();
        Files = record.Files.Select(file => new PassFileViewModel(file)).ToList();
    }

    /// <summary>Every station in the unit, including the ones turned away.</summary>
    public IReadOnlyList<PassStationViewModel> Stations { get; }

    /// <summary>The bounded account of what the files did.</summary>
    public IReadOnlyList<PassFileViewModel> Files { get; }

    public bool HasFiles => Files.Count > 0;

    /// <summary>The folders walked and how much was in them.</summary>
    /// <remarks>
    /// The one line nothing else on this machine records. For a station filed
    /// by date these are the dated sub-folders the cycle actually expanded to,
    /// which is the difference between "the vendor has stopped writing" and
    /// "the agent is looking in yesterday".
    /// </remarks>
    public string Walked => _record.Folders.Count == 0
        ? "Walked no folders — this station's filenames are built rather than listed."
        : string.Join(
            "   ",
            _record.Folders.Select(folder => string.Create(
                CultureInfo.CurrentCulture, $"{folder.Folder} ({folder.Entries} entries)")));

    /// <summary>Why the pass stopped, when it did not finish.</summary>
    public string Stopped => _record.Stopped ?? "";

    public bool WasCutShort => _record.Stopped is not null;

    /// <summary>The ADL this pass was made against.</summary>
    /// <remarks>
    /// Shown because a repoint deliberately leaves the logs alone, so a
    /// machine's log can hold passes from two instances -- and station link
    /// ids the newer one has since issued to entirely different stations.
    /// </remarks>
    public string Instance => _record.Instance;

    /// <summary>The whole pass as text, for the clipboard.</summary>
    public string Text => CycleRecordText.Render(_record);
}

/// <summary>One station's share of one pass.</summary>
public sealed class PassStationViewModel
{
    private readonly CycleStationRecord _station;

    public PassStationViewModel(CycleStationRecord station)
    {
        _station = station;
    }

    public string Station => string.IsNullOrWhiteSpace(_station.Station)
        ? _station.StationLinkId.ToString(CultureInfo.CurrentCulture)
        : string.Create(
            CultureInfo.CurrentCulture, $"{_station.StationLinkId}  {_station.Station}");

    public int Scanned => _station.Scanned;

    public int Held => _station.Held;

    public int Offered => _station.Offered;

    public int Wanted => _station.Wanted;

    public int Uploaded => _station.Uploaded;

    public int Failed => _station.Failed;

    public int Backlog => _station.Backlog;

    public string Error => _station.Error ?? "";

    public bool HasError => _station.Error is not null;
}

/// <summary>One file, or one group of files that did the same thing.</summary>
public sealed class PassFileViewModel
{
    private readonly CycleFileRecord _file;

    public PassFileViewModel(CycleFileRecord file)
    {
        _file = file;
    }

    /// <summary>
    /// The filename, or what the tally stands for when there is no one file.
    /// </summary>
    /// <remarks>
    /// The record folds identical failures into a line with a count and one
    /// example, and samples uploads with the remainder as a number. Both
    /// arrive here without a name, and a blank cell would read as a file whose
    /// name the agent failed to record.
    /// </remarks>
    public string Name => _file.Name ?? string.Create(
        CultureInfo.CurrentCulture, $"({_file.Count} more)");

    public string Size => _file.Size switch
    {
        null => "",
        < 1024 => string.Create(CultureInfo.CurrentCulture, $"{_file.Size} B"),
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{_file.Size / 1024.0:0.0} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{_file.Size / (1024.0 * 1024.0):0.0} MB"),
    };

    public string Outcome => _file.Outcome;

    /// <summary>How many files this line stands for. One, unless folded.</summary>
    public string Count => _file.Count == 1
        ? ""
        : _file.Count.ToString(CultureInfo.CurrentCulture);

    public string Reason => _file.Reason ?? "";

    public bool Failed => _file.Outcome == FileOutcomes.Failed;
}
