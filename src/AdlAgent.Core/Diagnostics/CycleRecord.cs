namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// What one unit did in one pass: the whole story, in one line of the cycle
/// log.
/// </summary>
/// <remarks>
/// Self-contained on purpose, which is the whole of why the record is a unit
/// pass and not an event. One record answers one question -- "what happened
/// to Bobo-Dioulasso at 13:24?" -- without joining anything to anything, and
/// eviction that deletes whole files therefore expires whole stories rather
/// than the halves of them that would make a reader think a station had gone
/// quiet.
/// <para>
/// A unit is identified by the folder path it walked. There is no stable unit
/// id anywhere in this product, and inventing one to put in a log would be
/// inventing a fact.
/// </para>
/// </remarks>
public sealed record CycleRecord
{
    /// <summary>
    /// The ADL this pass was made against.
    /// </summary>
    /// <remarks>
    /// Carried because a repoint deliberately leaves the logs alone -- a
    /// repoint is very often performed <em>because</em> something was wrong,
    /// and destroying the evidence at the moment somebody is investigating is
    /// the worst available timing. What that costs is records whose station
    /// link ids belong to an instance this machine no longer talks to, and
    /// this is what stops them being read as current.
    /// <para>
    /// Stamped by <see cref="CycleLog"/> rather than by whoever built the
    /// record: the cycle does not know which ADL it is talking to, and asking
    /// it to would be threading a fact about configuration through four
    /// methods about folders.
    /// </para>
    /// </remarks>
    public string Instance { get; init; } = "";

    /// <summary>When the pass started.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>How long it took, in seconds.</summary>
    public required double Seconds { get; init; }

    /// <summary>The folder this unit is named by: the first one it walked.</summary>
    public required string Unit { get; init; }

    /// <summary>What started it: see <see cref="CycleTriggers"/>.</summary>
    public required string Trigger { get; init; }

    /// <summary>False when the pass was cut short, and then <see cref="Stopped"/> says why.</summary>
    public required bool Completed { get; init; }

    /// <summary>Why the pass did not finish, when it did not.</summary>
    public string? Stopped { get; init; }

    /// <summary>
    /// The folders actually walked, and how many entries each held.
    /// </summary>
    /// <remarks>
    /// For a station filed by date these are the dated sub-folders the cycle
    /// expanded to, which nothing anywhere else records -- and which is the
    /// difference between "the vendor has stopped writing" and "the agent is
    /// looking in yesterday".
    /// </remarks>
    public required IReadOnlyList<CycleFolderRecord> Folders { get; init; }

    /// <summary>Every station in the unit, including the ones the scan turned away.</summary>
    public required IReadOnlyList<CycleStationRecord> Stations { get; init; }

    /// <summary>A bounded account of the files that did something.</summary>
    public required IReadOnlyList<CycleFileRecord> Files { get; init; }
}

/// <summary>One folder, walked once.</summary>
/// <param name="Entries">
/// Everything the walk went past, not what matched. Zero is the number that
/// tells "this folder is not there, or this machine cannot see it" apart from
/// "your pattern does not match what is in it".
/// </param>
public sealed record CycleFolderRecord(string Folder, int Entries);

/// <summary>One station's share of one pass.</summary>
/// <remarks>
/// The station name is carried beside the id because a record is read months
/// later, by somebody who has the log and not the ADL that issued the ids.
/// </remarks>
public sealed record CycleStationRecord
{
    public required long StationLinkId { get; init; }

    public string? Station { get; init; }

    /// <summary>Files in the folder matching this station's pattern.</summary>
    public required int Scanned { get; init; }

    /// <summary>Files left alone because they were still being written.</summary>
    public required int Held { get; init; }

    /// <summary>Files put in front of ADL.</summary>
    public required int Offered { get; init; }

    /// <summary>Files ADL asked for.</summary>
    public required int Wanted { get; init; }

    /// <summary>Files ADL took.</summary>
    public required int Uploaded { get; init; }

    /// <summary>Files that did not go.</summary>
    public required int Failed { get; init; }

    /// <summary>What this machine has that ADL does not.</summary>
    public required int Backlog { get; init; }

    /// <summary>
    /// What went wrong for this station, if anything did -- including the
    /// reason a station the scan turned away was turned away.
    /// </summary>
    /// <remarks>
    /// A half-configured station -- no folder, no pattern, Direct Fetch
    /// settings that do not add up -- is a common real fault and was
    /// invisible everywhere. It appears here with a zero in every count and
    /// the sentence that says what to fix.
    /// </remarks>
    public string? Error { get; init; }
}

/// <summary>
/// One thing that happened to one file, or to a group of files that failed
/// the same way.
/// </summary>
/// <param name="Count">
/// How many files this line stands for. One, except where a group was folded:
/// every distinct failure reason keeps a count and one example, and uploads
/// keep a sample and a tally.
/// </param>
/// <param name="Name">The example filename, or <c>null</c> for a pure tally.</param>
public sealed record CycleFileRecord
{
    public required string Outcome { get; init; }

    public string? Name { get; init; }

    public long? Size { get; init; }

    public long? StationLinkId { get; init; }

    public string? Reason { get; init; }

    public required int Count { get; init; }
}

/// <summary>What a file did, as the record spells it.</summary>
/// <remarks>
/// Strings and not an enum, because these are read out of a file by tools
/// nobody has written yet, and a number whose meaning lives in a C# enum is a
/// number nobody outside this program can read.
/// </remarks>
public static class FileOutcomes
{
    /// <summary>ADL asked for it and took it.</summary>
    public const string Uploaded = "uploaded";

    /// <summary>It did not go, and there is a reason.</summary>
    public const string Failed = "failed";

    /// <summary>It was still being written, so it was left for next time.</summary>
    public const string Held = "held";

    /// <summary>It matched, but it is older than the floor ADL put under this station.</summary>
    public const string Skipped = "skipped";

    /// <summary>It was in the folder and belongs to no station this unit serves.</summary>
    public const string Unmatched = "unmatched";
}

/// <summary>What started a pass.</summary>
public static class CycleTriggers
{
    /// <summary>The check interval came round.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>
    /// The same tick, on the day this unit's stations offer everything they
    /// have rather than only what the candidate window admits.
    /// </summary>
    public const string Reconciliation = "reconciliation";

    /// <summary>Somebody at the machine pressed Collect now.</summary>
    public const string Collect = "collect";
}
