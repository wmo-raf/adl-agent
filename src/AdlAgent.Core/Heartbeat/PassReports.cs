using AdlAgent.Core.Api;
using AdlAgent.Core.Diagnostics;

namespace AdlAgent.Core.Heartbeat;

/// <summary>
/// One unit pass, cut down from what the machine keeps to what ADL stores.
/// </summary>
/// <remarks>
/// The two records are deliberately not the same shape. A
/// <see cref="CycleRecord"/> carries a bounded account of every file the pass
/// touched -- the uploads, the sample, the tally, every distinct failure
/// reason -- and runs to kilobytes, because it is read by a technician
/// standing at the machine looking at one pass. What crosses the wire is read
/// by an operator in Nairobi asking about a fortnight, and becomes a row per
/// station in a country's database. So the folder list becomes a count, the
/// file account becomes three names, and everything else survives intact.
/// </remarks>
public static class PassReports
{
    /// <summary>
    /// How many names of files that did not arrive travel with a pass.
    /// </summary>
    /// <remarks>
    /// Three. Enough to see that a folder has filled up with files whose
    /// names no longer match, and few enough that the field is a hundred
    /// bytes on a row that already costs more than that. The whole account is
    /// on the machine for anyone who needs the rest of it.
    /// </remarks>
    public const int MostMissing = 3;

    /// <summary>
    /// The outcomes that mean "seen, and did not arrive", in the order a
    /// reader wants them.
    /// </summary>
    /// <remarks>
    /// Failures first, because a failure is the one with a sentence attached
    /// and somebody to act on it. Unmatched second, because it is the quiet
    /// one -- a vendor that renamed its files looks, from every other number
    /// in this product, exactly like a folder with nothing in it. Held last,
    /// because a file still being written is usually nothing at all and is
    /// only interesting when it is still held an hour later.
    /// <para>
    /// Uploads are absent by definition, and so is <c>skipped</c>: a file
    /// behind the floor ADL itself put under the station did not fail to
    /// arrive, it was not wanted.
    /// </para>
    /// </remarks>
    private static readonly string[] Outcomes =
    [
        FileOutcomes.Failed,
        FileOutcomes.Unmatched,
        FileOutcomes.Held,
    ];

    /// <summary>The pass as ADL is told it.</summary>
    public static CyclePassReport Of(CycleRecord record) =>
        new()
        {
            At = record.At,
            Seconds = record.Seconds,
            Unit = record.Unit,
            Trigger = record.Trigger,
            Completed = record.Completed,
            Stopped = record.Stopped,
            Folders = record.Folders.Count,
            Stations = record.Stations
                .Select(station => new CyclePassStation
                {
                    StationLinkId = station.StationLinkId,
                    Scanned = station.Scanned,
                    Held = station.Held,
                    Offered = station.Offered,
                    Wanted = station.Wanted,
                    Uploaded = station.Uploaded,
                    Failed = station.Failed,
                    Backlog = station.Backlog,
                    Error = station.Error,
                })
                .ToList(),
            Missing = Missing(record.Files),
        };

    /// <summary>
    /// Up to <see cref="MostMissing"/> named files that did not arrive.
    /// </summary>
    /// <remarks>
    /// Taken a round at a time across the three outcomes rather than in
    /// priority order, so that a pass with forty failures in it still spends
    /// one of its three slots on the unmatched name -- which is the one
    /// nothing else in this product would ever say. Priority decides only
    /// what fills the slots nobody else wanted.
    /// <para>
    /// Only named entries. A journal folds an outcome nobody had room to name
    /// into a pure tally, and a row saying "one more failure, no name, no
    /// reason" is the shape of an answer without being one.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<CyclePassFile> Missing(IReadOnlyList<CycleFileRecord> files)
    {
        var groups = Outcomes
            .Select(outcome => files
                .Where(file => file.Outcome == outcome && !string.IsNullOrWhiteSpace(file.Name))
                .ToList())
            .ToList();

        var picked = new List<CyclePassFile>(MostMissing);

        for (var round = 0; picked.Count < MostMissing; round++)
        {
            var anyLeft = false;

            foreach (var group in groups)
            {
                if (round >= group.Count)
                {
                    continue;
                }

                anyLeft = true;

                var file = group[round];

                picked.Add(new CyclePassFile
                {
                    Name = file.Name!,
                    Outcome = file.Outcome,
                    Reason = file.Reason,
                    StationLinkId = file.StationLinkId,
                });

                if (picked.Count == MostMissing)
                {
                    return picked;
                }
            }

            if (!anyLeft)
            {
                break;
            }
        }

        return picked;
    }
}
