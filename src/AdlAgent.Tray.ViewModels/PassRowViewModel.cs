using System;
using System.Globalization;
using AdlAgent.Core.Diagnostics;

namespace AdlAgent.Tray;

/// <summary>
/// One recorded pass, as a line in the table.
/// </summary>
/// <remarks>
/// The numbers are their own columns rather than a sentence, which is the
/// whole reason for having a table at all: a column of figures lines itself
/// up, and the one station in a shared dump directory whose numbers differ
/// from its neighbours' is then findable by eye. The text form pads that
/// column by hand and can only ever be read one row at a time.
/// </remarks>
public sealed class PassRowViewModel : Observable
{
    private readonly CyclePassRow _row;

    public PassRowViewModel(CyclePassRow row)
    {
        _row = row;
    }

    /// <summary>When the pass started. Half of the key that fetches its detail.</summary>
    public DateTimeOffset At => _row.At;

    /// <summary>The folder it walked. The other half of that key.</summary>
    public string Unit => _row.Unit;

    /// <summary>
    /// The folder, or a sentence when there is none.
    /// </summary>
    /// <remarks>
    /// A unit with no folder is a station the scan turned away -- no folder
    /// bound, no pattern, Direct Fetch settings that do not add up -- and an
    /// empty cell would read as a column the service failed to fill rather
    /// than as the fault it is.
    /// </remarks>
    public string Folder => string.IsNullOrWhiteSpace(_row.Unit)
        ? "(no folder bound)"
        : _row.Unit;

    public string When => _row.At.ToLocalTime().ToString("dd MMM HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>What started it, as a person would say it.</summary>
    public string Trigger => _row.Trigger switch
    {
        CycleTriggers.Scheduled => "scheduled",
        CycleTriggers.Reconciliation => "sweep",
        CycleTriggers.Collect => "collect now",
        _ => _row.Trigger,
    };

    public string Took => string.Create(CultureInfo.CurrentCulture, $"{_row.Seconds:0.0}s");

    public int Scanned => _row.Scanned;

    public int Held => _row.Held;

    public int Uploaded => _row.Uploaded;

    public int Failed => _row.Failed;

    public int Backlog => _row.Backlog;

    /// <summary>
    /// True when something about this pass was wrong, however it was wrong.
    /// </summary>
    /// <remarks>
    /// What the marker in the first column reads off. One mark over three
    /// unrelated faults, because a technician scanning forty rows for trouble
    /// is looking for "not this one" and not for a taxonomy.
    /// </remarks>
    public bool Problem => _row.Problem;

    /// <summary>The station these counts belong to, or null when they are the unit's.</summary>
    public string? Station => _row.Station;

    /// <summary>A sentence, when a station in this pass carries one.</summary>
    public string Error => _row.Error ?? "";

    public bool HasError => _row.Error is not null;

    /// <summary>What the marker column says when the pointer rests on it.</summary>
    public string Mark => !_row.Completed
        ? "This pass did not finish."
        : _row.Failed > 0
            ? "Files did not go."
            : HasError
                ? "A station in this pass has something wrong with it."
                : "";
}
