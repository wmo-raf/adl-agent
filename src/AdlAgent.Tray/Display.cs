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
}
