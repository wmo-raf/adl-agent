using System.Globalization;
using AdlAgent.Core.Api;

namespace AdlAgent.Core.Cycle;

/// <summary>
/// The dated sub-folders one station's files actually sit in, newest first.
/// </summary>
/// <remarks>
/// A vendor that files by date does not write into the folder an
/// administrator typed; it writes into <c>2026\08\21</c> below it. ADL has
/// let a station say so for as long as the FTP plugin has existed
/// (<c>dir_structured_by_date</c>, a granularity, and how the month is
/// spelled), and this is what turns that into directories to walk.
/// <para>
/// <b>The tree is carved in the station's timezone, not this machine's.</b>
/// A country server set to UTC serving Nairobi stations would spend the first
/// three hours of every local day walking yesterday's folder, find the day's
/// files nowhere, and say nothing was wrong. So the instant is converted to
/// the station's zone and the folder names are read off that -- which is also
/// what ADL and the FTP plugin do, and the three have to agree or a file
/// lands somewhere nobody looks.
/// </para>
/// <para>
/// <b>An ordinary cycle walks a short recent window.</b> Expanding from the
/// collection start date to now is 8,760 directories for a year at hour
/// granularity -- an enumeration each, every ten minutes, for a station whose
/// files are nearly all in the newest one or two. So a routine cycle takes
/// <see cref="DefaultRecentWindow"/> (ADL may say otherwise) and walking the
/// whole tree back to the start date is the reconciliation sweep's job, once
/// a day. What the window cuts off is not lost; it is simply not this
/// cycle's business.
/// </para>
/// <para>
/// <b>A folder that is not there yet is a non-event.</b> Nothing here asks
/// whether a directory exists -- the walk answers that, by finding nothing in
/// it -- because the newest directory in a live tree does not exist until the
/// vendor writes the day's first file, which on a ten-minute cycle is a state
/// every station passes through every single day.
/// </para>
/// </remarks>
public sealed class DatedFolders
{
    /// <summary>
    /// How far back an ordinary cycle walks, absent ADL saying otherwise.
    /// </summary>
    /// <remarks>
    /// Two days rather than one, so that the window never turns on the exact
    /// moment a local midnight passes: a station whose vendor writes the
    /// day's last file a few minutes late, on a machine whose cycle happens
    /// to land just after midnight, would otherwise have that file fall out
    /// of the window before it was ever offered. At hour granularity two days
    /// is 49 directories, which is a walk of a live tree and nothing more; at
    /// day granularity it is three, and at month or year it is one or two.
    /// <para>
    /// Everything older is the reconciliation sweep's, and the sweep is what
    /// makes this number safe to keep small.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan DefaultRecentWindow = TimeSpan.FromHours(48);

    /// <summary>
    /// The most directories an ordinary cycle will expand to, whatever window
    /// ADL asks for.
    /// </summary>
    /// <remarks>
    /// A backstop on the setting rather than the setting itself. An
    /// administrator who types 8760 hours into the device's window meant "a
    /// year" and did not mean "walk eight thousand directories every ten
    /// minutes on every machine in the fleet", and the difference between
    /// those two readings is a country's link.
    /// </remarks>
    public const int MostPerCycle = 2_000;

    /// <summary>
    /// The most a reconciliation cycle will expand to, which is the whole
    /// tree for any station filed at a sensible granularity.
    /// </summary>
    /// <remarks>
    /// Ten thousand is thirteen months of hourly directories, twenty-seven
    /// years of daily ones, and any span at all of monthly or yearly ones. A
    /// station that wants more than that is one filing hourly against a
    /// collection start date years back, and it is told its oldest folders
    /// are out of reach rather than left to discover it.
    /// <para>
    /// Still a bound, because these are enumerations and not stat calls: the
    /// sweep runs once a day and must still finish inside one.
    /// </para>
    /// </remarks>
    public const int MostPerSweep = 10_000;

    private DatedFolders(IReadOnlyList<IReadOnlyList<string>> segments, bool truncated, string? problem)
    {
        Segments = segments;
        Truncated = truncated;
        Problem = problem;
    }

    /// <summary>
    /// The sub-folder each directory sits at, newest first, as path segments
    /// below the folder ADL named.
    /// </summary>
    /// <remarks>
    /// Segments rather than paths, because joining them onto the station's
    /// folder is path grammar and path grammar belongs to the platform --
    /// see <see cref="Platform.IFileMetadataSource.Descend"/>.
    /// </remarks>
    public IReadOnlyList<IReadOnlyList<string>> Segments { get; }

    /// <summary>
    /// True when the bound stopped the expansion before the floor did, so
    /// this cycle looked only at the newest part of the tree.
    /// </summary>
    public bool Truncated { get; }

    /// <summary>
    /// What is wrong with this station's dated-folder settings, if anything
    /// is. A station with a problem expands to nothing: guessing at half a
    /// folder convention would have it walk directories no vendor writes and
    /// report that it found no files, which is the silence this product
    /// exists to remove.
    /// </summary>
    public string? Problem { get; }

    /// <summary>
    /// How far back an ordinary cycle walks, given what ADL said.
    /// </summary>
    /// <remarks>
    /// Absent (an ADL that predates the setting) is the default. Zero or less
    /// is an administrator asking for the current period alone, which is a
    /// real choice for a machine on a link that cannot afford more, and is
    /// obeyed rather than clamped away -- the current period is always walked
    /// whatever the window says.
    /// </remarks>
    public static TimeSpan RecentWindow(int? hours) => hours switch
    {
        null => DefaultRecentWindow,
        <= 0 => TimeSpan.Zero,
        _ => TimeSpan.FromHours(Math.Min(hours.Value, 24 * 365 * 10)),
    };

    /// <summary>
    /// The directories <paramref name="config"/> implies between
    /// <paramref name="from"/> and <paramref name="now"/>.
    /// </summary>
    /// <param name="timezoneId">
    /// The station's timezone -- HQ's tier, the zone the tree is carved in.
    /// </param>
    /// <param name="from">
    /// The oldest instant whose directory is worth walking, or <c>null</c> for
    /// no floor at all, where <paramref name="mostDirectories"/> is the only
    /// bound there is.
    /// </param>
    /// <param name="mostDirectories">
    /// <see cref="MostPerCycle"/> on an ordinary cycle,
    /// <see cref="MostPerSweep"/> on a reconciling one.
    /// </param>
    public static DatedFolders For(
        StationLinkAppConfig config,
        string? timezoneId,
        DateTimeOffset? from,
        DateTimeOffset now,
        int mostDirectories)
    {
        if (!TryGranularity(config.DateGranularity, out var granularity))
        {
            return Problematic(string.IsNullOrWhiteSpace(config.DateGranularity)
                ? "This station's files are in dated sub-folders but nothing says how far down the tree they "
                    + "sit. Set its date granularity in ADL to year, month, day or hour."
                : $"This station files by '{config.DateGranularity}', which is not a folder granularity this "
                    + "agent knows. Set it to year, month, day or hour in ADL.");
        }

        if (MonthDirectory(1, config.MonthDirFormat) is null)
        {
            return Problematic(
                $"'{config.MonthDirFormat}' is not a month folder format this agent can write. "
                + "Set it to m, n, M, b, F or f in ADL.");
        }

        if (!StationZone.TryResolve(timezoneId, out var zone))
        {
            return Problematic(
                $"This machine does not know the timezone '{timezoneId}', so it cannot tell which dated "
                + "sub-folders this station's files are in.");
        }

        // Civil time throughout, and deliberately. A directory is named for a
        // whole hour or a whole day, so which instant inside it the clock
        // changed on cannot matter -- and stepping back a month or a year has
        // no meaning as a duration anyway. Comparing the floor in the same
        // civil terms keeps the walk monotone across a daylight-saving change
        // instead of building an hour the zone deleted.
        var local = Truncate(TimeZoneInfo.ConvertTime(now, zone).DateTime, granularity);
        var floor = from is null ? (DateTime?)null : TimeZoneInfo.ConvertTime(from.Value, zone).DateTime;

        var segments = new List<IReadOnlyList<string>>();

        while (true)
        {
            segments.Add(Directory(local, granularity, config.MonthDirFormat));

            // The period holding the floor counts as above it: a folder named
            // for the 21st holds the whole of the 21st, so a floor at nine in
            // the morning still wants it.
            if (floor is not null && local <= floor)
            {
                return new DatedFolders(segments, truncated: false, problem: null);
            }

            if (segments.Count >= mostDirectories || local.Year <= 1)
            {
                // The year guard is not a real bound and is never the one
                // that stops a station: it is here so that a caller passing
                // no floor and a large count cannot walk a civil clock off
                // the end of the calendar.
                return new DatedFolders(segments, truncated: true, problem: null);
            }

            local = Step(local, granularity);
        }
    }

    private static DatedFolders Problematic(string problem) => new([], truncated: false, problem: problem);

    /// <summary>The folder names one period is filed under, outermost first.</summary>
    private static IReadOnlyList<string> Directory(
        DateTime local, DateGranularity granularity, string? monthDirFormat)
    {
        var segments = new List<string>(4) { local.Year.ToString(CultureInfo.InvariantCulture) };

        if (granularity == DateGranularity.Year)
        {
            return segments;
        }

        segments.Add(MonthDirectory(local.Month, monthDirFormat)!);

        if (granularity == DateGranularity.Month)
        {
            return segments;
        }

        segments.Add(local.Day.ToString("00", CultureInfo.InvariantCulture));

        if (granularity == DateGranularity.Day)
        {
            return segments;
        }

        segments.Add(local.Hour.ToString("00", CultureInfo.InvariantCulture));

        return segments;
    }

    /// <summary>
    /// How the month is spelled, or <c>null</c> when the format is not one of
    /// the six.
    /// </summary>
    /// <remarks>
    /// ADL's six, verbatim, and English whatever this machine's locale is:
    /// the vendor wrote the directory once and the agent has to spell it the
    /// same way from any country's server. Absent means <c>m</c>, which is
    /// what ADL defaults the field to and what the FTP plugin assumes.
    /// </remarks>
    private static string? MonthDirectory(int month, string? monthDirFormat)
    {
        var months = CultureInfo.InvariantCulture.DateTimeFormat;

        return (string.IsNullOrWhiteSpace(monthDirFormat) ? "m" : monthDirFormat) switch
        {
            "m" => month.ToString("00", CultureInfo.InvariantCulture),
            "n" => month.ToString(CultureInfo.InvariantCulture),
            "M" => months.GetAbbreviatedMonthName(month),
            "b" => months.GetAbbreviatedMonthName(month).ToLowerInvariant(),
            "F" => months.GetMonthName(month),
            "f" => months.GetMonthName(month).ToLowerInvariant(),
            _ => null,
        };
    }

    /// <summary>The start of the period <paramref name="local"/> falls in.</summary>
    private static DateTime Truncate(DateTime local, DateGranularity granularity) => granularity switch
    {
        DateGranularity.Year => new DateTime(local.Year, 1, 1),
        DateGranularity.Month => new DateTime(local.Year, local.Month, 1),
        DateGranularity.Day => local.Date,
        _ => new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0),
    };

    /// <summary>The period before this one.</summary>
    private static DateTime Step(DateTime local, DateGranularity granularity) => granularity switch
    {
        DateGranularity.Year => local.AddYears(-1),
        DateGranularity.Month => local.AddMonths(-1),
        DateGranularity.Day => local.AddDays(-1),
        _ => local.AddHours(-1),
    };

    /// <summary>
    /// ADL's four granularities, matched case-insensitively for the same
    /// reason <see cref="ListingStrategies"/> is: the cost of being wrong is
    /// a station that silently collects nothing, and there is no reading of
    /// "Day" from an ADL instance that could mean anything else.
    /// </summary>
    private static bool TryGranularity(string? granularity, out DateGranularity resolved)
    {
        foreach (var candidate in Enum.GetValues<DateGranularity>())
        {
            if (string.Equals(candidate.ToString(), granularity, StringComparison.OrdinalIgnoreCase))
            {
                resolved = candidate;

                return true;
            }
        }

        resolved = DateGranularity.Day;

        return false;
    }

    private enum DateGranularity
    {
        Year,
        Month,
        Day,
        Hour,
    }
}
