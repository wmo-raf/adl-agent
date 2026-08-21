using System.IO.Enumeration;
using AdlAgent.Core.Platform;

namespace AdlAgent.Windows.Platform;

/// <summary>
/// The file-metadata seam on Windows.
/// </summary>
/// <remarks>
/// Enumeration goes through <see cref="FileSystemEnumerable{TResult}"/> so
/// that each entry's size and times come out of the directory scan that found
/// it. On the folders this product exists for -- one flat directory holding a
/// minute-by-minute file per station, for every station in the country -- the
/// difference between reading the metadata the scan already has and stat'ing
/// each name afterwards is the difference between a cycle that finishes and
/// one that does not.
/// <para>
/// The windowing timestamp is the later of last-write and creation. Creation
/// time is not redundant on Windows: a file copied into the folder keeps the
/// source's last-write time, so a backfill dropped in today can carry a
/// timestamp from weeks ago, and windowing on last-write alone would let it
/// fall behind the watermark and never be offered.
/// </para>
/// </remarks>
public sealed class WindowsFileMetadataSource : IFileMetadataSource
{
    public IEnumerable<FileFacts> Enumerate(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            // A folder that is not there is a configuration problem for the
            // cycle to report against the station link, not an exception for
            // it to survive.
            yield break;
        }

        var entries = new FileSystemEnumerable<FileFacts>(
            folderPath,
            static (ref FileSystemEntry entry) => new FileFacts(
                entry.ToFullPath(),
                entry.FileName.ToString(),
                entry.Length,
                Window(entry.LastWriteTimeUtc, entry.CreationTimeUtc)),
            new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Directory | FileAttributes.ReparsePoint,
            })
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
        };

        using var enumerator = entries.GetEnumerator();

        while (true)
        {
            FileFacts facts;

            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }

                facts = enumerator.Current;
            }
            catch (IOException)
            {
                // The folder went away mid-scan, or the share dropped. What
                // was already found is still worth offering.
                yield break;
            }
            catch (UnauthorizedAccessException)
            {
                yield break;
            }

            yield return facts;
        }
    }

    public FileFacts? Describe(string folderPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            // Not a filename this filesystem can hold, so nothing is there to
            // describe. Refused here rather than joined and opened, because
            // some of these characters do not mean "invalid" to Windows so
            // much as "something else entirely": a colon names an alternate
            // data stream, and a station whose filename format carries one
            // (the FTP plugin offers YYYY-MM-DDTHH:MM:SS, which is a fine
            // name on the Unix servers it was written for) would otherwise
            // have the agent reading a stream of the folder itself.
            return null;
        }

        try
        {
            var info = new FileInfo(Path.Combine(folderPath, fileName));

            if (!info.Exists)
            {
                return null;
            }

            return new FileFacts(
                info.FullName,
                info.Name,
                info.Length,
                Window(info.LastWriteTimeUtc, info.CreationTimeUtc));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException)
        {
            return null;
        }
    }

    private static DateTimeOffset Window(DateTimeOffset lastWrite, DateTimeOffset creation) =>
        lastWrite > creation ? lastWrite : creation;

    private static DateTimeOffset Window(DateTime lastWriteUtc, DateTime creationUtc) =>
        Window(
            new DateTimeOffset(DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(creationUtc, DateTimeKind.Utc)));
}
