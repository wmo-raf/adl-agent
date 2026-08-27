using AdlAgent.Core.Api;

namespace AdlAgent.Core.Cycle;

/// <summary>
/// One station's share of a cycle, filled in as the cycle runs.
/// </summary>
/// <remarks>
/// This is what an operator in another country reads when a station goes
/// quiet, so the four numbers are chosen to tell apart the ways that happens:
/// nothing in the folder (<see cref="Scanned"/> zero), nothing new
/// (<see cref="Offered"/> above zero and <see cref="Uploaded"/> zero because
/// ADL already held it), or something wrong (<see cref="Failed"/>).
/// <para>
/// The counts are moved by this class's own methods rather than by whoever
/// is holding it. A cycle has three places where a file can be lost -- the
/// scan, a refused page, a refused upload -- and all three mean the same
/// thing to the station: one more file that did not go, and a sentence
/// saying why.
/// </para>
/// <para>
/// Safe to move from several threads, which is a narrower claim than it
/// sounds. Two <em>units</em> never share a tally -- a unit owns its stations
/// outright, which is what grouping by shared folders is for -- so nothing
/// here is guarding against the machine at large. What it guards is one
/// unit's own uploads running several at a time: those are all this station's
/// files, and they all come home to this station's counters.
/// </para>
/// </remarks>
public sealed class LinkTally
{
    public LinkTally(long stationLinkId)
    {
        StationLinkId = stationLinkId;
    }

    public long StationLinkId { get; }

    /// <summary>Files in the folder matching this station's pattern.</summary>
    public int Scanned => Volatile.Read(ref _scanned);

    /// <summary>Files put in front of ADL this cycle.</summary>
    public int Offered => Volatile.Read(ref _offered);

    /// <summary>Files ADL asked for. Not reported; it is what backlog is measured from.</summary>
    public int Requested => Volatile.Read(ref _requested);

    /// <summary>Files ADL accepted.</summary>
    public int Uploaded => Volatile.Read(ref _uploaded);

    /// <summary>Files that could not be sent, and will be tried again next cycle.</summary>
    public int Failed => Volatile.Read(ref _failed);

    /// <summary>
    /// Files seen but left alone, because they were still being written.
    /// </summary>
    /// <remarks>
    /// Deliberately not counted as failures. In a live folder the newest file
    /// is in this state on every single cycle, and a station permanently
    /// reporting a failure is a station nobody looks at any more.
    /// </remarks>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>What went wrong for this station, if anything did.</summary>
    public string? Error => Volatile.Read(ref _error);

    /// <summary>Files this machine has seen that ADL does not hold.</summary>
    public int Backlog => Pending + Math.Max(0, Requested - Uploaded);

    /// <summary>A file in the folder matched this station's pattern.</summary>
    public void Saw() => Interlocked.Increment(ref _scanned);

    /// <summary>A file was left alone until it has finished being written.</summary>
    public void Wait() => Interlocked.Increment(ref _pending);

    /// <summary>A file went into a manifest.</summary>
    public void Offer() => Interlocked.Increment(ref _offered);

    /// <summary>ADL asked for a file this station offered.</summary>
    public void Want() => Interlocked.Increment(ref _requested);

    /// <summary>ADL took a file.</summary>
    public void Accept() => Interlocked.Increment(ref _uploaded);

    /// <summary>A file did not go, and here is what to tell whoever asks.</summary>
    public void Fail(string reason)
    {
        Interlocked.Increment(ref _failed);
        Note(reason);
    }

    /// <summary>
    /// Record what went wrong, keeping the first.
    /// </summary>
    /// <remarks>
    /// The first rather than the last: when a station has several problems in
    /// one cycle the later ones are usually consequences of the earlier one,
    /// and one sentence is all the fleet listing shows.
    /// <para>
    /// "First" is decided by whichever upload gets there first once several
    /// run at a time, which is as it should be: they are the same station's
    /// files failing for the same reason, and a race between two spellings of
    /// one fault is not a race worth ordering.
    /// </para>
    /// </remarks>
    public void Note(string error) => Interlocked.CompareExchange(ref _error, error, null);

    private int _scanned;
    private int _pending;
    private int _offered;
    private int _requested;
    private int _uploaded;
    private int _failed;
    private string? _error;

    public CycleLinkReport ToReport() => new()
    {
        StationLinkId = StationLinkId,
        Scanned = Scanned,
        Offered = Offered,
        Uploaded = Uploaded,
        Failed = Failed,
        Error = Error,
    };
}
