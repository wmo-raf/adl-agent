namespace AdlAgent.Core.Cycle;

/// <summary>
/// The timezone a station's names are written in, resolved once and the same
/// way wherever it is asked for.
/// </summary>
/// <remarks>
/// Two quite different things are named in a station's configuration by a
/// timezone, and both of them decide which file the agent looks for: the
/// zone a DIRECT_FETCH station's filenames are stamped in
/// (<c>direct_fetch_datetime_timezone</c>), and the zone a dated folder tree
/// is carved in (the station's own <c>timezone</c>, HQ's tier). Resolving
/// them differently would be a bug nobody could see, so they resolve here.
/// <para>
/// A name this machine cannot resolve is a problem to say out loud and never
/// a fallback. Falling back to UTC would have an East African station look
/// in a folder three hours from the one its vendor writes, find nothing, and
/// report nothing wrong -- for ever.
/// </para>
/// <para>
/// It can genuinely fail. ADL sends IANA names ("Africa/Nairobi"), and
/// Windows resolves those through a mapping that lives in ICU -- which the
/// operating system supplies from Windows 10 / Server 2019 onwards and older
/// Windows does not have at all. On a Server 2016 machine (the tested floor)
/// a station whose files are named or filed in local time may therefore land
/// here.
/// </para>
/// </remarks>
internal static class StationZone
{
    /// <summary>The timezone ADL named, or UTC when it named none.</summary>
    public static bool TryResolve(string? id, out TimeZoneInfo zone)
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
