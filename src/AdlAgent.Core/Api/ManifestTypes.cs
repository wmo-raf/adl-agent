namespace AdlAgent.Core.Api;

/// <summary>
/// One candidate file, as the agent describes it to ADL.
/// </summary>
/// <remarks>
/// The same four facts travel twice: once in the manifest, where they are a
/// proposal, and again with the upload, where ADL checks the bytes against
/// them and refuses anything that disagrees. One record for both, because a
/// file described one way when it was offered and another way when it was
/// sent is the one thing the ledger cannot survive.
/// </remarks>
public sealed record ManifestEntry
{
    public required long StationLinkId { get; init; }

    /// <summary>The bare filename. ADL's ledger knows the file by it.</summary>
    public required string Name { get; init; }

    public required long Size { get; init; }

    /// <summary>
    /// The file's time, as the file-metadata seam reports it.
    /// </summary>
    /// <remarks>
    /// ADL calls this field <c>mtime</c> and keeps it in the ledger for
    /// people to read; it never diffs on it. What the agent has is the one
    /// timestamp the seam hands over -- on Windows the later of last-write
    /// and creation -- so that is what is sent, and it is the same number the
    /// candidate window was measured against.
    /// </remarks>
    public required DateTimeOffset Mtime { get; init; }

    /// <summary>The file's sha-256, lowercase hex. ADL diffs on exactly this.</summary>
    public required string Hash { get; init; }
}

/// <summary>A page of candidates, as the manifest endpoint reads it.</summary>
public sealed record ManifestRequest
{
    public IReadOnlyList<ManifestEntry> Files { get; init; } = [];
}

/// <summary>What ADL made of one page of candidates.</summary>
/// <remarks>
/// Every answer ADL gives carries the configuration version and the limits,
/// so that the two things a machine most needs to keep in step ride the calls
/// it makes most often rather than waiting for the next sync.
/// </remarks>
public sealed record ManifestResponse
{
    public long ConfigVersion { get; init; }

    public AgentLimits Limits { get; init; } = new();

    /// <summary>The files to send, echoing the hash each was offered under.</summary>
    public IReadOnlyList<RequestedFile> Requested { get; init; } = [];

    /// <summary>
    /// Station links ADL does not recognise as this device's, and station
    /// links an administrator has switched off.
    /// </summary>
    /// <remarks>
    /// Reported rather than refused, because a machine works from a cached
    /// configuration and will sometimes offer files for a link that has since
    /// moved or been disabled. Worth surfacing per station: "ADL is ignoring
    /// Garissa" is a sentence a technician can act on, and silence is not.
    /// </remarks>
    public IReadOnlyList<long> UnknownStationLinks { get; init; } = [];

    public IReadOnlyList<long> DisabledStationLinks { get; init; } = [];
}

/// <summary>One file ADL asked for, identified as it was offered.</summary>
/// <remarks>
/// The hash comes back with the name so the agent can match the answer to the
/// candidate it proposed rather than re-reading the file to work out which
/// version of it this is -- which on a folder that is still being written to
/// would sometimes be a different version.
/// </remarks>
public sealed record RequestedFile
{
    public long StationLinkId { get; init; }
    public string Name { get; init; } = "";
    public string Hash { get; init; } = "";
}

/// <summary>What ADL now holds under that name.</summary>
/// <remarks>
/// Read back rather than assumed: ADL hashes the bytes itself and answers
/// with what it stored, so an upload the agent believes in and an upload ADL
/// believes in are the same upload. The agent acts on the refusals, not on
/// this -- a success here is worth logging and nothing more.
/// </remarks>
public sealed record UploadResponse
{
    public long StationLinkId { get; init; }
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public string Hash { get; init; } = "";

    /// <summary>received, processed, or failed -- the staging row's state.</summary>
    public string Status { get; init; } = "";

    public DateTimeOffset? ReceivedAt { get; init; }
    public long ConfigVersion { get; init; }
}
