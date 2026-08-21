namespace AdlAgent.Core.Platform;

/// <summary>
/// Platform seam 1 of 4: file metadata.
/// </summary>
/// <remarks>
/// Enumeration lives here rather than in the core because metadata and
/// enumeration are the same platform call: a directory walk hands back each
/// entry's times and size with the entry, and a core that listed names and
/// then stat'ed them would pay for the same information twice on exactly the
/// folders (tens of thousands of files) where that cost is the problem.
/// <para>
/// Implementations must stream. Nothing in the agent ever needs the whole
/// listing in memory at once, and the folders this runs against are the
/// reason.
/// </para>
/// </remarks>
public interface IFileMetadataSource
{
    /// <summary>
    /// Every file directly inside <paramref name="folderPath"/>, lazily.
    /// An unreadable or missing folder yields nothing rather than throwing --
    /// a vendor folder that has not been created yet is a configuration
    /// problem to report, not a cycle to abandon.
    /// </summary>
    IEnumerable<FileFacts> Enumerate(string folderPath);

    /// <summary>
    /// One named file, or <c>null</c> if it is not there. This is the whole
    /// of what the DIRECT_FETCH strategy needs from the filesystem.
    /// </summary>
    FileFacts? Describe(string path);
}
