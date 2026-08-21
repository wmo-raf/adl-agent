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

/// <summary>
/// What one pass over this device's folders found.
/// </summary>
/// <remarks>
/// <see cref="Candidates"/> is a sequence and not a list on purpose: the
/// files come out newest first, and each is read and hashed only as it is
/// taken. A caller that offers them a page at a time therefore reads a page
/// at a time -- which is what lets a fresh install put today's observations
/// on the wire before it has touched last year's. It can be walked once.
/// </remarks>
public sealed record ScanResult(
    IEnumerable<FileCandidate> Candidates,
    IReadOnlyDictionary<long, LinkTally> Links)
{
    /// <summary>
    /// This station's tally, or <c>null</c> if the scan opened none for it.
    /// </summary>
    /// <remarks>
    /// Null happens: ADL can name a station link in a manifest answer that
    /// this device did not scan, because its cached configuration and ADL's
    /// have moved apart. Answering with null rather than throwing is what
    /// keeps one stale station from costing a machine the rest of its cycle.
    /// </remarks>
    public LinkTally? For(long stationLinkId) =>
        Links.TryGetValue(stationLinkId, out var tally) ? tally : null;

    /// <summary>Tell this station something, if it is one of ours.</summary>
    public void Note(long stationLinkId, string message) => For(stationLinkId)?.Note(message);
}
