using AdlAgent.Core.Api;

namespace AdlAgent.Core.Cycle;

/// <summary>
/// One file worth offering: what ADL will be told about it, and where it is.
/// </summary>
/// <remarks>
/// The path is kept beside the entry because the upload needs both and they
/// must be the same file. Under the enumerate strategy the path is the one
/// the platform seam handed over, never one the core assembled -- which is
/// what lets a test describe a Windows folder on a machine that has no C:
/// drive.
/// </remarks>
public sealed record FileCandidate(string Path, ManifestEntry Entry);

/// <summary>What one pass over this device's folders found.</summary>
public sealed record ScanResult(
    IReadOnlyList<FileCandidate> Candidates,
    IReadOnlyDictionary<long, LinkTally> Links);

/// <summary>
/// One station's share of a cycle, filled in as the cycle runs.
/// </summary>
/// <remarks>
/// This is what an operator in another country reads when a station goes
/// quiet, so the four numbers are chosen to tell apart the ways that happens:
/// nothing in the folder (<see cref="Scanned"/> zero), nothing new
/// (<see cref="Offered"/> above zero and <see cref="Uploaded"/> zero because
/// ADL already held it), or something wrong (<see cref="Failed"/>).
/// </remarks>
public sealed class LinkTally
{
    public LinkTally(long stationLinkId)
    {
        StationLinkId = stationLinkId;
    }

    public long StationLinkId { get; }

    /// <summary>Files in the folder matching this station's pattern.</summary>
    public int Scanned { get; set; }

    /// <summary>Files put in front of ADL this cycle.</summary>
    public int Offered { get; set; }

    /// <summary>Files ADL asked for. Not reported; it is what backlog is measured from.</summary>
    public int Requested { get; set; }

    /// <summary>Files ADL accepted.</summary>
    public int Uploaded { get; set; }

    /// <summary>Files that could not be sent, and will be tried again next cycle.</summary>
    public int Failed { get; set; }

    /// <summary>
    /// Files seen but left alone, because they were still being written.
    /// </summary>
    /// <remarks>
    /// Deliberately not counted as failures. In a live folder the newest file
    /// is in this state on every single cycle, and a station permanently
    /// reporting a failure is a station nobody looks at any more.
    /// </remarks>
    public int Pending { get; set; }

    /// <summary>What went wrong for this station, if anything did.</summary>
    public string? Error { get; private set; }

    /// <summary>Files this machine has seen that ADL does not hold.</summary>
    public int Backlog => Pending + Math.Max(0, Requested - Uploaded);

    /// <summary>
    /// Record what went wrong, keeping the first.
    /// </summary>
    /// <remarks>
    /// The first rather than the last: when a station has several problems in
    /// one cycle the later ones are usually consequences of the earlier one,
    /// and one sentence is all the fleet listing shows.
    /// </remarks>
    public void Note(string error) => Error ??= error;

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
