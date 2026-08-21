using System.Globalization;
using AdlAgent.Core.Api;

namespace AdlAgent.Core.Cycle;

/// <summary>
/// The filenames a DIRECT_FETCH station expects to exist, newest first.
/// </summary>
/// <remarks>
/// The escape hatch for the folder nobody wants to think about: a single
/// directory holding a minute-by-minute file for every station in the
/// country, going back years. Walking it is the problem -- not hashing, not
/// uploading, the walk itself -- so a station configured this way never walks
/// anything. Its vendor names files to a clock, so the agent builds the names
/// the clock implies and asks the filesystem about those exact ones. A name
/// that is not there is not an error and not a message: on a cadence of one
/// file every ten minutes, the file for the ten minutes that have not
/// finished yet is missing on every cycle for ever.
/// <para>
/// Two clocks are in play and they are not the same one. The interval is a
/// cadence in real time, so the walk steps in absolute instants; the name is
/// written in the station's own <c>direct_fetch_datetime_timezone</c>, so
/// each instant is converted before it is formatted. Both are read off the
/// one instant, which is what keeps a file near a local midnight named for
/// the day it belongs to.
/// </para>
/// <para>
/// Instants are aligned to the interval as the vendor sees it -- measured
/// from local midnight, not from the Unix epoch. A country on a half-hour or
/// quarter-hour offset writes <c>...0500</c>, <c>...0510</c> where a UTC grid
/// would look for <c>...0445</c>, <c>...0455</c>, and every single name would
/// miss.
/// </para>
/// </remarks>
public sealed class ExpectedFiles
{
    /// <summary>
    /// The most instants an ordinary cycle will construct a name for.
    /// </summary>
    /// <remarks>
    /// A bound rather than a tuning knob. ADL's watermark is a floor and does
    /// not move (it is the administrator's collection start date), so the
    /// number of expected files grows without limit as an install ages: a
    /// station on a one-minute cadence with a start date two years back
    /// expects a million files, and a cycle that stat'ed every one of them
    /// every ten minutes would never finish. Twenty thousand is a fortnight
    /// of minute files, four months of ten-minute files, and a few tens of
    /// milliseconds of stat calls. Newest first, so what the bound cuts off
    /// is always the oldest history and never today.
    /// <para>
    /// What it cuts off is not lost, because the bound is not the only pass
    /// this station gets -- see <see cref="MostPerSweep"/>.
    /// </para>
    /// </remarks>
    public const int MostPerCycle = 20_000;

    /// <summary>
    /// The most a reconciliation cycle will construct, which is the whole
    /// backlog for any station that is busy rather than misconfigured.
    /// </summary>
    /// <remarks>
    /// The reason a DIRECT_FETCH station is reconciled at all. It has no
    /// folder to re-walk and nothing a lower floor would reveal, so it would
    /// seem to have nothing to reconcile -- except that
    /// <see cref="MostPerCycle"/> stops an ordinary cycle short of its oldest
    /// backlog, and ADL's floor never moves to bring that backlog closer. A
    /// file recovered and copied in three weeks late (story 15) would then be
    /// looked for on no cycle, ever. So the rare pass decision #267 leaves
    /// room for is the deep one: once a day this station asks about every
    /// name back to its collection start date.
    /// <para>
    /// Still a bound, because the names are built before they are asked
    /// about and half a million strings is where that stops being free.
    /// Half a million is a year of minute-by-minute files or a decade of
    /// ten-minute ones; a station wanting more than that has a collection
    /// start date nobody meant to set, and is told so.
    /// </para>
    /// </remarks>
    public const int MostPerSweep = 500_000;

    private ExpectedFiles(IReadOnlyList<string> names, bool truncated, string? problem)
    {
        Names = names;
        Truncated = truncated;
        Problem = problem;
    }

    /// <summary>The names to ask the filesystem about, newest first, each once.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>
    /// True when <see cref="MostPerCycle"/> stopped the walk before the floor
    /// did, so this cycle looked only at the newest part of the backlog.
    /// </summary>
    public bool Truncated { get; }

    /// <summary>
    /// What is wrong with this station's Direct Fetch settings, if anything
    /// is. A station with a problem builds no names at all: guessing at half
    /// a filename convention would have it silently collect nothing.
    /// </summary>
    public string? Problem { get; }

    /// <summary>
    /// The names <paramref name="config"/> implies between
    /// <paramref name="floor"/> and <paramref name="now"/>.
    /// </summary>
    /// <param name="floor">
    /// The oldest instant worth a name -- ADL's watermark for the station, or
    /// its collection start date. Null means ADL put no floor under this
    /// station at all, and then <paramref name="mostNames"/> is the only
    /// bound there is.
    /// </param>
    /// <param name="mostNames">
    /// How many instants to walk back over: <see cref="MostPerCycle"/> on an
    /// ordinary cycle, <see cref="MostPerSweep"/> on a reconciling one.
    /// </param>
    public static ExpectedFiles For(
        StationLinkAppConfig config, DateTimeOffset? floor, DateTimeOffset now, int mostNames)
    {
        if (config.DirectFetchIntervalMinutes is not > 0)
        {
            return Problematic(
                "No file interval is set for this station, so there is no clock to build its filenames from.");
        }

        if (string.IsNullOrWhiteSpace(config.DirectFetchDatetimeFormat))
        {
            return Problematic(
                "No filename datetime format is set for this station, so its filenames cannot be built.");
        }

        if (!TryResolveZone(config.DirectFetchDatetimeTimezone, out var zone))
        {
            return Problematic(
                $"This machine does not know the timezone '{config.DirectFetchDatetimeTimezone}', "
                + "so it cannot tell which filenames to look for.");
        }

        var interval = TimeSpan.FromMinutes(config.DirectFetchIntervalMinutes.Value);
        var prefix = config.DirectFetchPrefix ?? "";
        var extension = config.DirectFetchFileExtension ?? "";
        var format = config.DirectFetchDatetimeFormat!;

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moment = AlignedAtOrBefore(now, zone, interval);
        var steps = 0;

        while (steps < mostNames && !Below(moment, interval, floor))
        {
            steps++;

            string name;

            try
            {
                name = prefix
                    + TimeZoneInfo.ConvertTime(moment, zone).ToString(format, CultureInfo.InvariantCulture)
                    + extension;
            }
            catch (FormatException)
            {
                return Problematic(
                    $"'{format}' is not a filename datetime format this agent can write.");
            }

            if (name.Contains('/', StringComparison.Ordinal) ||
                name.Contains('\\', StringComparison.Ordinal))
            {
                // A separator in the format would send the agent looking
                // somewhere below the folder ADL named. Refused rather than
                // followed: what a station may read is an administrator's
                // decision.
                //
                // The platform seam refuses such a name too, and more
                // thoroughly -- it knows what its filesystem will not hold.
                // The two are not the same check. The seam can only answer
                // "not there", which for a constructed name is the ordinary
                // reply and says nothing; this one exists to put a sentence
                // in front of the person who typed the format.
                return Problematic(
                    $"The filename this station builds ('{name}') contains a folder separator.");
            }

            // Deduplicated, and not as a tidiness measure. A ten-minute
            // cadence named YYYYMMDD is a hundred and forty-four instants
            // sharing one filename -- a real configuration, for a logger
            // appending to one file a day -- and offering it a hundred and
            // forty-four times would fill every manifest page with the same
            // entry.
            //
            // Against every name seen and not merely the last one, because a
            // format can repeat without the repeats being neighbours: HHmm
            // names the same file once a day, and the two are separated by a
            // day's worth of other names.
            if (seen.Add(name))
            {
                names.Add(name);
            }

            // Re-aligned each step rather than simply stepping back by the
            // interval, so that a daylight-saving change moves the grid with
            // the vendor instead of leaving the whole backlog an hour off it.
            moment = AlignedAtOrBefore(moment - interval, zone, interval);
        }

        return new ExpectedFiles(names, truncated: !Below(moment, interval, floor), problem: null);
    }

    private static ExpectedFiles Problematic(string problem) => new([], truncated: false, problem: problem);

    /// <summary>
    /// True when the walk has gone past the oldest instant worth a name.
    /// </summary>
    /// <remarks>
    /// The interval containing the floor counts as above it, not below: a
    /// file named for 09:00 holds the ten minutes after 09:00, so a floor of
    /// 09:05 still wants it.
    /// </remarks>
    private static bool Below(DateTimeOffset moment, TimeSpan interval, DateTimeOffset? floor) =>
        floor is not null && moment + interval <= floor;

    /// <summary>
    /// The latest instant at or before <paramref name="instant"/> that sits on
    /// the vendor's grid.
    /// </summary>
    /// <remarks>
    /// The subtraction is done on the instant rather than by rebuilding a
    /// local time, so an hour that a daylight-saving change deleted is never
    /// constructed -- <see cref="DateTimeOffset"/> would happily hold one and
    /// the name built from it would be for a file no logger ever wrote.
    /// <para>
    /// The grid is measured from local midnight, so an interval of a whole
    /// day or more has nothing left to take off and lands on midnight itself
    /// -- right for the daily and weekly files that are the only sensible
    /// readings of such an interval, and near enough for the ones that are
    /// not (a thirty-six-hour cadence lands on every second midnight). Left
    /// unclamped deliberately: the interval is a vendor's cadence, and an
    /// agent that quietly rounded it would be looking for filenames nobody
    /// writes rather than for filenames that are merely rare.
    /// </para>
    /// </remarks>
    private static DateTimeOffset AlignedAtOrBefore(
        DateTimeOffset instant, TimeZoneInfo zone, TimeSpan interval)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);

        return instant.AddTicks(-(local.TimeOfDay.Ticks % interval.Ticks));
    }

    /// <summary>
    /// The timezone ADL named, or UTC when it named none.
    /// </summary>
    /// <remarks>
    /// A name this machine cannot resolve is a problem and not a fallback.
    /// Falling back to UTC would have an East African station look for files
    /// three hours from the ones its vendor writes, find none of them, and
    /// report nothing wrong -- for ever.
    /// <para>
    /// It can genuinely fail. ADL sends IANA names ("Africa/Nairobi"), and
    /// Windows resolves those through a mapping that lives in ICU -- which
    /// the operating system supplies from Windows 10 / Server 2019 onwards
    /// and older Windows does not have at all. On a Server 2016 machine (the
    /// tested floor) a station whose filenames are written in local time may
    /// therefore land here. That it says so is the whole point: the sentence
    /// reaches the fleet listing through the cycle report, where a station
    /// silently looking for the wrong filenames would not.
    /// </para>
    /// </remarks>
    private static bool TryResolveZone(string? id, out TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            zone = TimeZoneInfo.Utc;

            return true;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);

            return true;
        }
        catch (Exception exception) when (
            exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;

            return false;
        }
    }
}
