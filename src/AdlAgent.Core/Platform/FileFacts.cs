namespace AdlAgent.Core.Platform;

/// <summary>
/// One file, as the agent needs to know it: what it is called, how big it is,
/// and the single timestamp the candidate window is measured against.
/// </summary>
/// <remarks>
/// There is deliberately no "creation time" and no "last write time" here.
/// Which clock a platform should window on is the platform's business --
/// Windows takes the later of last-write and creation, Linux takes statx
/// birth time where the filesystem has it and mtime where it does not -- and
/// the moment the core could see both it would start choosing between them.
/// The seam hands over one answer, already chosen.
/// </remarks>
public readonly record struct FileFacts
{
    public FileFacts(string path, string name, long length, DateTimeOffset windowTimestamp)
    {
        Path = path;
        Name = name;
        Length = length;
        WindowTimestamp = windowTimestamp;
    }

    /// <summary>Full path, as the platform spells it.</summary>
    public string Path { get; }

    /// <summary>Bare filename, which is the name ADL's ledger knows it by.</summary>
    public string Name { get; }

    public long Length { get; }

    /// <summary>
    /// The timestamp the candidate window filters on, in UTC. See the remarks
    /// on <see cref="FileFacts"/> for why there is only one.
    /// </summary>
    public DateTimeOffset WindowTimestamp { get; }
}
