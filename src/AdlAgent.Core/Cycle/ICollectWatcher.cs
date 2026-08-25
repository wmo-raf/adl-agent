namespace AdlAgent.Core.Cycle;

/// <summary>
/// Somebody watching a collect-now while it runs.
/// </summary>
/// <remarks>
/// A seam rather than a reference to the thing that actually watches, so that
/// <see cref="UploadCycle"/> -- which is the product -- does not acquire a
/// dependency on the local UI's plumbing. The cycle says where it has got to
/// and hands over the tally; what is done with either is somebody else's
/// business, and in every test but the ones about the window it is nobody's.
/// <para>
/// <see cref="Counting"/> hands over the live object rather than a copy of its
/// numbers. The counts move throughout the delivery -- a page offered here, a
/// file accepted there -- and a watcher polled every second wants the state at
/// the moment it asked, which is what reading the tally gives it and what any
/// snapshot pushed at it would not.
/// </para>
/// </remarks>
public interface ICollectWatcher
{
    /// <summary>Say which part of the cycle this now is.</summary>
    void Step(string step);

    /// <summary>
    /// Hand over the station's tally, so the counts can be read as they move.
    /// </summary>
    /// <param name="tally">
    /// Null when the scan opened none for this station, which happens when HQ
    /// has it -- or its connection -- switched off. There is then nothing to
    /// count and nothing will move.
    /// </param>
    void Counting(LinkTally? tally);

    /// <summary>A watcher for a run nobody is watching.</summary>
    public static ICollectWatcher Nobody { get; } = new NoOne();

    private sealed class NoOne : ICollectWatcher
    {
        public void Step(string step)
        {
        }

        public void Counting(LinkTally? tally)
        {
        }
    }
}
