namespace AdlAgent.Core.Platform;

/// <summary>
/// Platform seam 1 of 5: file metadata.
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
    /// One named file inside <paramref name="folderPath"/>, or <c>null</c> if
    /// it is not there. This is the whole of what the DIRECT_FETCH strategy
    /// needs from the filesystem.
    /// </summary>
    /// <remarks>
    /// A folder and a name rather than a path, because joining them is path
    /// grammar and path grammar is the platform's business. The core knows
    /// what a station's folder is called (an administrator typed it) and what
    /// a file in it is called (a clock and a format imply it); which
    /// separator goes between them, and what that means when the folder is
    /// spelled <c>C:</c> or ends in a slash already, is a question it should
    /// never have to answer.
    /// <para>
    /// A name that is not there is the ordinary case and not an error: on a
    /// ten-minute cadence the file for the ten minutes that have not finished
    /// yet is absent on every single cycle.
    /// </para>
    /// </remarks>
    FileFacts? Describe(string folderPath, string fileName);

    /// <summary>
    /// The folder <paramref name="segments"/> names below
    /// <paramref name="folderPath"/> -- <c>2026</c>, <c>08</c>, <c>21</c>
    /// under the folder an administrator typed.
    /// </summary>
    /// <remarks>
    /// Here rather than in the core for the same reason
    /// <see cref="Describe"/> takes a folder and a name separately: joining
    /// is path grammar, and path grammar is the platform's business. The core
    /// knows which dated directory a station's files are in (a granularity
    /// and a clock imply it) and never which separator goes between the parts
    /// of a path, nor what that means when the folder is spelled <c>C:</c> or
    /// ends in a separator already.
    /// <para>
    /// The answer is also the key two station links sharing a dated tree are
    /// grouped under, so two spellings of one folder must join to one string
    /// or the tree is walked twice.
    /// </para>
    /// </remarks>
    string Descend(string folderPath, IReadOnlyList<string> segments);
}
