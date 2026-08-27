namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// The files one unit pass did something to, bounded by usefulness rather
/// than by a count.
/// </summary>
/// <remarks>
/// A flat "newest N" cap was tried in the design and rejected, because the
/// two shapes a bad pass takes defeat it in opposite directions. A machine on
/// its first bind offers five hundred files a page and uploads hundreds per
/// pass for hours; a station whose share has unmounted fails every file with
/// one identical sentence, five hundred times. Under a flat cap the first
/// fills the record with names nobody will read, and the second fills it with
/// five hundred copies of one sentence -- and throws away the one interesting
/// anomaly further down, which is the only line in it worth having.
/// <para>
/// So each outcome is bounded by what makes it useful:
/// </para>
/// <list type="bullet">
/// <item>every <b>distinct failure reason</b> is kept once, with a count and
/// one example filename -- five hundred identical failures are one line, and
/// the one different failure among them is another;</item>
/// <item><b>held</b>, <b>skipped</b> and <b>unmatched</b> files are kept up to
/// a small cap, because a handful of examples answers the question and the
/// rest are the same answer;</item>
/// <item><b>uploaded</b> files are sampled, with the remainder as a tally,
/// because what a reader wants from a good pass is proof of what moved and a
/// number.</item>
/// </list>
/// <para>
/// A catastrophic pass is therefore no longer than a quiet one, and still
/// says the right thing.
/// </para>
/// <para>
/// Safe to move from several threads, which is a narrower claim than it
/// sounds: a journal belongs to one unit, and a unit owns its stations
/// outright. What it guards is that unit's own uploads running several at a
/// time.
/// </para>
/// </remarks>
public sealed class UnitJournal
{
    /// <summary>How many uploaded filenames are kept before the rest become a tally.</summary>
    public const int UploadedSample = 20;

    /// <summary>How many held, skipped or unmatched files are kept, each.</summary>
    public const int NotedCap = 10;

    /// <summary>
    /// How many distinct failure reasons are kept.
    /// </summary>
    /// <remarks>
    /// High, because distinct reasons are the expensive thing to lose and
    /// there are not many of them: a pass with more than this many genuinely
    /// different faults in it is a pass whose first fifty say what is wrong.
    /// It is a bound and not a target -- something has to stop a reason built
    /// from a filename, which is a shape that has existed here before.
    /// </remarks>
    public const int ReasonCap = 50;

    private readonly Lock _gate = new();

    private readonly List<CycleFileRecord> _uploaded = [];
    private readonly List<CycleFileRecord> _held = [];
    private readonly List<CycleFileRecord> _skipped = [];
    private readonly List<CycleFileRecord> _unmatched = [];

    /// <summary>Distinct failure reasons, in the order they were first seen.</summary>
    private readonly Dictionary<string, Failure> _failures = new(StringComparer.Ordinal);
    private readonly List<string> _reasons = [];

    private int _uploadedTotal;
    private int _heldTotal;
    private int _skippedTotal;
    private int _unmatchedTotal;
    private int _reasonsOverflowed;

    /// <summary>ADL took this file.</summary>
    public void Uploaded(string name, long size, long stationLinkId) =>
        Sample(_uploaded, ref _uploadedTotal, UploadedSample, FileOutcomes.Uploaded, name, size, stationLinkId);

    /// <summary>This file did not go, and here is the reason on its own.</summary>
    /// <remarks>
    /// The reason without the filename in it, which is what makes the folding
    /// work. Everywhere else in the agent a failure sentence is built as
    /// "name: reason" for an operator to read, and if that string arrived here
    /// every one of five hundred identical faults would be a distinct reason.
    /// </remarks>
    public void Failed(string name, long? size, long stationLinkId, string reason)
    {
        lock (_gate)
        {
            if (_failures.TryGetValue(reason, out var seen))
            {
                _failures[reason] = seen with { Count = seen.Count + 1 };

                return;
            }

            if (_reasons.Count >= ReasonCap)
            {
                _reasonsOverflowed++;

                return;
            }

            _reasons.Add(reason);
            _failures[reason] = new Failure(name, size, stationLinkId, 1);
        }
    }

    /// <summary>This file was still being written.</summary>
    public void Held(string name, long size, long stationLinkId, string reason) =>
        Sample(_held, ref _heldTotal, NotedCap, FileOutcomes.Held, name, size, stationLinkId, reason);

    /// <summary>This file is behind the floor ADL put under its station.</summary>
    public void Skipped(string name, long size, long stationLinkId) =>
        Sample(_skipped, ref _skippedTotal, NotedCap, FileOutcomes.Skipped, name, size, stationLinkId);

    /// <summary>
    /// This entry was in a folder this unit walked and belongs to no station
    /// in it.
    /// </summary>
    /// <remarks>
    /// No station link id: that is the point of it. A vendor writing
    /// <c>BOB_20260827.DAT</c> into a folder configured for
    /// <c>BOB_*.dat</c> looks, from every other number in this product, like
    /// a folder with nothing in it.
    /// </remarks>
    public void Unmatched(string name, long size) =>
        Sample(_unmatched, ref _unmatchedTotal, NotedCap, FileOutcomes.Unmatched, name, size, null);

    /// <summary>The bounded account, as it goes into the record.</summary>
    /// <remarks>
    /// Uploads first, because on a working machine that is the answer;
    /// failures last, because on a broken one that is where the eye stops.
    /// </remarks>
    public IReadOnlyList<CycleFileRecord> Files()
    {
        lock (_gate)
        {
            var files = new List<CycleFileRecord>();

            files.AddRange(_uploaded);
            Remainder(files, FileOutcomes.Uploaded, Volatile.Read(ref _uploadedTotal) - _uploaded.Count);

            files.AddRange(_held);
            Remainder(files, FileOutcomes.Held, Volatile.Read(ref _heldTotal) - _held.Count);

            files.AddRange(_skipped);
            Remainder(files, FileOutcomes.Skipped, Volatile.Read(ref _skippedTotal) - _skipped.Count);

            files.AddRange(_unmatched);
            Remainder(
                files, FileOutcomes.Unmatched, Volatile.Read(ref _unmatchedTotal) - _unmatched.Count);

            foreach (var reason in _reasons)
            {
                var failure = _failures[reason];

                files.Add(new CycleFileRecord
                {
                    Outcome = FileOutcomes.Failed,
                    Name = failure.Example,
                    Size = failure.Size,
                    StationLinkId = failure.StationLinkId,
                    Reason = reason,
                    Count = failure.Count,
                });
            }

            if (_reasonsOverflowed > 0)
            {
                files.Add(new CycleFileRecord
                {
                    Outcome = FileOutcomes.Failed,
                    Reason = $"and {_reasonsOverflowed} further failures, of reasons this record has no room for",
                    Count = _reasonsOverflowed,
                });
            }

            return files;
        }
    }

    /// <summary>
    /// Count one, and name it if there is still room to.
    /// </summary>
    /// <remarks>
    /// The count is moved without the lock on purpose. <see cref="Skipped"/>
    /// is called for every file behind a station's watermark, which on a
    /// settled folder shared by forty stations is millions of calls per cycle
    /// -- and "a settled folder costs the walk and nothing else" is the
    /// promise the whole scan is built around. Past the cap this is one
    /// interlocked add and a return.
    /// </remarks>
    private void Sample(
        List<CycleFileRecord> kept,
        ref int total,
        int cap,
        string outcome,
        string name,
        long size,
        long? stationLinkId,
        string? reason = null)
    {
        if (Interlocked.Increment(ref total) > cap)
        {
            return;
        }

        lock (_gate)
        {
            // Asked again inside the lock, because the count above is not the
            // list's length once several uploads are running at once.
            if (kept.Count >= cap)
            {
                return;
            }

            kept.Add(new CycleFileRecord
            {
                Outcome = outcome,
                Name = name,
                Size = size,
                StationLinkId = stationLinkId,
                Reason = reason,
                Count = 1,
            });
        }
    }

    /// <summary>The ones there was no room to name, as a number.</summary>
    private static void Remainder(List<CycleFileRecord> files, string outcome, int remaining)
    {
        if (remaining <= 0)
        {
            return;
        }

        files.Add(new CycleFileRecord { Outcome = outcome, Count = remaining });
    }

    /// <summary>One failure reason: how many times, and one file it happened to.</summary>
    private readonly record struct Failure(string Example, long? Size, long StationLinkId, int Count);
}
