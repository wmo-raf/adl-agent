namespace AdlAgent.Core.Platform;

/// <summary>
/// Platform seam 2 of 5: is this file safe to read yet?
/// </summary>
/// <remarks>
/// The question is one question but the answer is not portable. On Windows a
/// vendor process holding a file open can be caught by trying to open it for
/// shared reading; on Linux nothing in the filesystem says a writer is still
/// writing, and the stability window is all there is. Putting the whole
/// judgement behind the seam is what keeps the core from growing an
/// <c>if (windows)</c> around the part that differs.
/// </remarks>
public interface IFileReadinessProbe
{
    /// <summary>
    /// True when <paramref name="file"/> can be shipped as it stands.
    /// </summary>
    /// <param name="stabilityWindow">
    /// How long a file must have been untouched to count as finished. Per
    /// station link, and configured in ADL.
    /// </param>
    bool IsReadyToRead(FileFacts file, TimeSpan stabilityWindow, DateTimeOffset now);
}
