using System;
using System.Globalization;

namespace AdlAgent.Tray;

/// <summary>How this window writes the few things that are not text already.</summary>
/// <remarks>
/// Shared between the header and the station rows because a moment shown two
/// ways on one screen reads as two different moments -- and because the
/// machine's own timezone is the only one a technician standing at it thinks
/// in, whatever ADL sent.
/// </remarks>
internal static class Display
{
    /// <summary>A moment in this machine's own timezone, or a dash.</summary>
    public static string Moment(DateTimeOffset? value) =>
        value is null ? "-" : value.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>
    /// How long ago something was, as a bare span: "19 hours", "4 days".
    /// </summary>
    /// <remarks>
    /// Coarse on purpose. The exact moment is a column of its own and never
    /// stale; what this adds is the reading a person does at a glance, and
    /// "19 hours" and "19 hours 14 minutes" are the same reading. One unit,
    /// the largest that is not zero, so the string cannot grow wide enough to
    /// need a column.
    /// <para>
    /// A moment in the future -- which a machine whose clock is behind ADL's
    /// will produce, and ADL measures exactly that skew -- is not negative
    /// time. It reads as "moments", because the honest thing to say about a
    /// file that arrived a minute from now is that it has just arrived.
    /// </para>
    /// </remarks>
    public static string Span(DateTimeOffset from, DateTimeOffset now)
    {
        var elapsed = now - from;

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "moments";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return Counted((int)elapsed.TotalMinutes, "minute");
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return Counted((int)elapsed.TotalHours, "hour");
        }

        return Counted((int)elapsed.TotalDays, "day");
    }

    /// <summary>The same span as something a sentence can end on.</summary>
    public static string Ago(DateTimeOffset from, DateTimeOffset now) =>
        $"{Span(from, now)} ago";

    private static string Counted(int count, string unit) => count == 1
        ? $"1 {unit}"
        : string.Create(CultureInfo.CurrentCulture, $"{count} {unit}s");
}
