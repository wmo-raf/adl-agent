namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// One pass as a line in a table: everything a row shows and nothing it does
/// not.
/// </summary>
/// <remarks>
/// The file detail is deliberately absent, and that absence is what makes a
/// table of passes possible at all. The control surface caps a message at
/// 64 KB; a full record carries its file list and runs to kilobytes, so
/// twenty of them fill a page, and a busy machine writes some fifteen hundred
/// passes a day -- seventy pages for one day, which is not a table anybody
/// can scan for a moment. A row is about a hundred and twenty bytes, so a
/// page is four hundred and fifty of them.
/// <para>
/// The detail is fetched for one record when a row is opened, which is the
/// only time anybody wants it.
/// </para>
/// </remarks>
public sealed record CyclePassRow
{
    /// <summary>When the pass started. Half of the key that fetches its detail.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The folder the pass walked. The other half of that key.</summary>
    public required string Unit { get; init; }

    public required string Trigger { get; init; }

    public required double Seconds { get; init; }

    public required bool Completed { get; init; }

    /// <summary>
    /// The station these counts belong to, or <c>null</c> when they are the
    /// unit's own.
    /// </summary>
    /// <remarks>
    /// Filled in only when the read was filtered to one station, and the
    /// window renames the column to match. Without it a technician filtering
    /// to Banfora in a forty-station dump directory would read Bobo-
    /// Dioulasso's twelve failures as Banfora's -- a station misread as
    /// broken, which is the exact failure this whole record exists to
    /// prevent.
    /// </remarks>
    public string? Station { get; init; }

    /// <summary>
    /// The counts a column shows, and only those.
    /// </summary>
    /// <remarks>
    /// Offered and wanted are deliberately not here. They are in the detail,
    /// where a station's line spells all seven, and a row's whole purpose is
    /// being light enough that hundreds of them cross one control message.
    /// </remarks>
    public required int Scanned { get; init; }

    public required int Held { get; init; }

    public required int Uploaded { get; init; }

    public required int Failed { get; init; }

    public required int Backlog { get; init; }

    /// <summary>
    /// True when something about this pass was wrong.
    /// </summary>
    /// <remarks>
    /// One flag over three unrelated faults -- files that did not go, a pass
    /// cut short, a station turned away half-configured with zero counts and
    /// a sentence. They have nothing in common except that a technician
    /// hunting trouble wants all of them and should not have to know which
    /// kind they are looking for.
    /// </remarks>
    public required bool Problem { get; init; }

    /// <summary>A sentence, when one station in the pass carries one.</summary>
    /// <remarks>
    /// The first, because the row has space for one and a unit's stations
    /// usually fail together for the same reason. The rest are in the
    /// detail.
    /// </remarks>
    public string? Error { get; init; }

    /// <summary>This record as a row, counted the way the query asked for.</summary>
    public static CyclePassRow Of(CycleRecord record, long? stationLinkId)
    {
        var station = stationLinkId is null
            ? null
            : record.Stations.FirstOrDefault(
                candidate => candidate.StationLinkId == stationLinkId);

        return new CyclePassRow
        {
            At = record.At,
            Unit = record.Unit,
            Trigger = record.Trigger,
            Seconds = record.Seconds,
            Completed = record.Completed,
            Station = station?.Station,
            Scanned = station?.Scanned ?? record.Stations.Sum(entry => entry.Scanned),
            Held = station?.Held ?? record.Stations.Sum(entry => entry.Held),
            Uploaded = station?.Uploaded ?? record.Stations.Sum(entry => entry.Uploaded),
            Failed = station?.Failed ?? record.Stations.Sum(entry => entry.Failed),
            Backlog = station?.Backlog ?? record.Stations.Sum(entry => entry.Backlog),
            // The whole pass, even when the counts are one station's: a unit
            // cut short was cut short for every station in it, and a
            // technician filtered to one of them still needs the mark.
            Problem = IsProblem(record),
            Error = station is not null
                ? station.Error
                : record.Stations.FirstOrDefault(entry => entry.Error is not null)?.Error,
        };
    }

    /// <summary>True when something about this pass was wrong.</summary>
    public static bool IsProblem(CycleRecord record) =>
        !record.Completed
        || record.Stations.Any(station => station.Failed > 0 || station.Error is not null);
}

/// <summary>
/// A page of rows, and an honest account of how it was arrived at.
/// </summary>
/// <remarks>
/// The three facts below exist so that the window can tell apart the two
/// answers that look identical and mean opposite things: "twelve passes had
/// problems" and "twelve passes had problems in the twenty thousand I got
/// through before I stopped looking". A page that reported only the first
/// would be the silent truncation this whole record was built to end -- twelve
/// rows and no caveat reads as a machine that failed twelve times.
/// </remarks>
public sealed record CyclePassIndex
{
    public required IReadOnlyList<CyclePassRow> Rows { get; init; }

    /// <summary>True when the read reached the end of this machine's log.</summary>
    public required bool Exhausted { get; init; }

    /// <summary>How many records were examined to build this page.</summary>
    public required int Scanned { get; init; }

    /// <summary>
    /// Where a further read carries on from: this page's skip plus what it
    /// examined.
    /// </summary>
    /// <remarks>
    /// Examined rather than returned. A filtered read that gave up part-way
    /// must resume where it gave up; resuming from the last row it matched
    /// would walk the same stretch of log again and give up in the same
    /// place, for ever.
    /// </remarks>
    public required int Resume { get; init; }
}
