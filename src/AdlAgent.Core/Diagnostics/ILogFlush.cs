namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// A log with something still in the air.
/// </summary>
/// <remarks>
/// Both logs are written through a background queue, which is the whole point
/// of them -- no log call may touch a disk on a cycle thread. The cost is that
/// at any instant the newest few records are in memory and not in the file,
/// and there is exactly one moment when that matters: somebody has pressed
/// "Save diagnostics…" and the thing they are about to email is read off
/// those same files.
/// <para>
/// An interface rather than two direct references, because the two logs are
/// registered in different places -- the cycle log in the core's container,
/// the general sink by the head while logging is being configured -- and a
/// bundle that had to know which of them exists on this tier would be a
/// bundle that quietly misses one. Asking for every registration means a head
/// that adds a third sink gets it flushed for free, and a head with none
/// resolves an empty list rather than failing.
/// </para>
/// </remarks>
public interface ILogFlush
{
    /// <summary>Wait until what has been written so far has reached the disk.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
