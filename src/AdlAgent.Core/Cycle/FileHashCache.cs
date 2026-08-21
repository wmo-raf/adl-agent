using System.Security.Cryptography;
using AdlAgent.Core.Platform;

namespace AdlAgent.Core.Cycle;

/// <summary>
/// The sha-256 of a file, remembered against the size and time it had.
/// </summary>
/// <remarks>
/// ADL diffs manifests on the hash, so every candidate has to carry one, and
/// computing it means reading the file. In a settled folder nothing has
/// changed since last cycle and every one of those reads is wasted -- which
/// on the folders this product exists for is the difference between a cycle
/// that finishes inside the check interval and one that does not.
/// <para>
/// Purely an optimisation, and safe to lose: the key is the file's own size
/// and windowing timestamp, so a file that changed cannot answer from the
/// cache, and a cache that was thrown away costs one re-hashing pass and
/// never a wrong answer. That is why it lives in memory and is not persisted
/// -- ADL is the only thing that remembers what it holds (decision #266).
/// </para>
/// </remarks>
public sealed class FileHashCache
{
    private readonly Lock _gate = new();

    // Keyed on the path exactly as the platform seam spelled it, compared
    // case-sensitively. Folding case would be a guess about somebody else's
    // filesystem: harmless on Windows, where it would at worst save a
    // re-hash, and wrong on Linux, where /data/a and /data/A are two files.
    private Dictionary<string, Entry> _known = new(StringComparer.Ordinal);
    private Dictionary<string, Entry> _touched = new(StringComparer.Ordinal);

    /// <summary>
    /// The file's sha-256 in lowercase hex, read from the disk only if this
    /// exact size and timestamp is not already known.
    /// </summary>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">This account may not read it.</exception>
    public string Hash(FileFacts file)
    {
        lock (_gate)
        {
            if (_known.TryGetValue(file.Path, out var known) && known.Describes(file))
            {
                _touched[file.Path] = known;

                return known.Hash;
            }
        }

        var hash = Read(file.Path);

        lock (_gate)
        {
            var entry = new Entry(file.Length, file.WindowTimestamp, hash);

            _known[file.Path] = entry;
            _touched[file.Path] = entry;
        }

        return hash;
    }

    /// <summary>
    /// Forget every file that was not looked at since the last call.
    /// </summary>
    /// <remarks>
    /// Called once the scan is done, which is when the working set is known.
    /// Without it the cache would be a record
    /// of every file the machine has ever seen -- including the ones a
    /// vendor's archiving job moved away years ago -- on a service that runs
    /// for months. With it, what is held is the working set: the files inside
    /// the candidate window, which is what the window is for.
    /// </remarks>
    public void Forget()
    {
        lock (_gate)
        {
            _known = _touched;
            _touched = new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
    }

    private static string Read(string path)
    {
        // Shared with a writer on purpose: the readiness probe has already
        // said this file is finished, and refusing to share would mean a
        // vendor process that merely holds its folder open could stop the
        // agent reading a file it has long since closed.
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 64 * 1024);

        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>One remembered file: what it was, and what it hashed to.</summary>
    private readonly record struct Entry(long Length, DateTimeOffset WindowTimestamp, string Hash)
    {
        public bool Describes(FileFacts file) =>
            file.Length == Length && file.WindowTimestamp == WindowTimestamp;
    }
}
