using AdlAgent.Core.Platform;

namespace AdlAgent.Windows.Platform;

/// <summary>
/// The file-readiness seam on Windows: a stability window, and then a knock
/// on the door.
/// </summary>
/// <remarks>
/// Two checks because neither is enough on its own. The window catches a
/// logger that writes a line every few seconds and never holds a lock; the
/// shared-read open catches a vendor process that opened its output file
/// exclusively an hour ago and is still filling it. Shipping either kind
/// half-written puts a truncated record through the decoder, and a decoder
/// that saw half a line has no way to know it.
/// <para>
/// This is the seam's Windows half specifically. A Linux head does the window
/// and stops there -- an open file on Linux says nothing about whether anyone
/// is writing to it -- which is exactly why the judgement lives behind an
/// interface instead of behind a flag.
/// </para>
/// </remarks>
public sealed class WindowsFileReadinessProbe : IFileReadinessProbe
{
    public bool IsReadyToRead(FileFacts file, TimeSpan stabilityWindow, DateTimeOffset now)
    {
        if (stabilityWindow > TimeSpan.Zero && now - file.WindowTimestamp < stabilityWindow)
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);

            return true;
        }
        catch (IOException)
        {
            // Held exclusively by whoever is writing it. Next cycle.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // The service account cannot read it. Not a partial file, but not
            // one this machine can ship either, and the answer is the same.
            return false;
        }
    }
}
