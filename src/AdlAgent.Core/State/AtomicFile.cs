namespace AdlAgent.Core.State;

/// <summary>
/// Replacing a file's contents in a way a power cut cannot half-do.
/// </summary>
/// <remarks>
/// Flushed to the disk under a temporary name and then moved into place, so
/// a crash mid-write leaves the previous contents rather than half of the new
/// ones. Country servers lose power, and every file this agent keeps is one
/// whose truncated remains would cost somebody a visit: a half-written token
/// file is a machine that has to be re-paired, and a half-written
/// <c>agent.ini</c> is a machine that has forgotten where it reports.
/// <para>
/// One implementation rather than one per caller, because the reasoning above
/// is the whole of what these writes have in common and is the part that is
/// easy to leave out of the second one.
/// </para>
/// </remarks>
public static class AtomicFile
{
    /// <summary>Put <paramref name="contents"/> at <paramref name="path"/>.</summary>
    public static void Write(string path, string contents)
    {
        var temporary = path + ".tmp";

        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file))
        {
            writer.Write(contents);
            writer.Flush();

            // On the disk, not merely in the operating system's hands, before
            // anything is moved over the file that is still good.
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
