namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// Which passes somebody is asking to see.
/// </summary>
/// <remarks>
/// Answered by the service while it reads the log, and never by the window
/// over the page it already has. The difference is the whole usefulness of
/// filtering: a page is meant to be a page of <em>matches</em>, so that
/// "Load more" walks back through rows rather than through blank screens,
/// and a failure three weeks back is reachable at all. A window that narrowed
/// what it had been sent could only ever narrow the newest few hundred
/// passes, which on a healthy machine contain nothing worth finding.
/// <para>
/// One record rather than five parameters, because these five travel
/// together everywhere: the index request, the detail request's neighbours,
/// the diagnostics bundle -- which takes the same filter so that what a
/// technician sends is what they were looking at -- and every test.
/// </para>
/// </remarks>
/// <param name="StationLinkId">
/// Only passes this station was in, or <c>null</c> for the machine's own.
/// A station rather than a unit, because a station's unit is whatever it
/// happens to share a folder with and nobody knows the name of that.
/// </param>
/// <param name="Trigger">
/// Only passes started this way -- see <see cref="CycleTriggers"/> -- or
/// <c>null</c> for any. The one filter that answers "did the nightly sweep
/// actually run", which nothing else on the machine can.
/// </param>
/// <param name="ProblemsOnly">
/// Only passes where something was wrong. See <see cref="CyclePassRow.Problem"/>:
/// one switch rather than three, because a technician looking for trouble
/// should not first have to know which of the three kinds it was.
/// </param>
/// <param name="Before">
/// Only passes older than this instant, which is how paging walks backwards.
/// The cursor is the oldest record <em>examined</em> rather than the oldest
/// returned, so resuming after a read that gave up part-way carries on from
/// where it gave up rather than from the last thing it happened to match.
/// </param>
/// <param name="Most">How many matching rows to return.</param>
public sealed record CyclePassQuery(
    long? StationLinkId = null,
    string? Trigger = null,
    bool ProblemsOnly = false,
    DateTimeOffset? Before = null,
    int Most = 50)
{
    /// <summary>True when this record is one of the ones being asked for.</summary>
    public bool Matches(CycleRecord record)
    {
        if (StationLinkId is not null &&
            !record.Stations.Any(station => station.StationLinkId == StationLinkId))
        {
            return false;
        }

        if (Trigger is not null &&
            !string.Equals(record.Trigger, Trigger, StringComparison.Ordinal))
        {
            return false;
        }

        return !ProblemsOnly || CyclePassRow.IsProblem(record);
    }
}
