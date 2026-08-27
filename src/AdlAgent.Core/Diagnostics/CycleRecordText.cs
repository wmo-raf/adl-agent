using System.Globalization;
using System.Text;

namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// A unit pass, written out for a person.
/// </summary>
/// <remarks>
/// JSON Lines is what is stored, and this is what is read. The two are kept
/// apart on purpose: the file has to be machine-readable because #307 will
/// put these records on the wire, and the thing a technician looks at has to
/// be readable on a ministry's server with Notepad and nothing else.
/// <para>
/// In the core, and used by both places a record is ever shown: the Check
/// status… window's recent passes, and the plain-text bundle somebody emails.
/// One renderer, so the pass a technician reads on screen and the pass HQ
/// reads in the attachment are the same sentences.
/// </para>
/// <para>
/// Times are written in this machine's own timezone. It is the only one a
/// person standing at the machine thinks in, and the instant is in the JSON
/// beside it for anybody who needs to line two countries up.
/// </para>
/// </remarks>
public static class CycleRecordText
{
    /// <summary>One record, as the block of lines it is.</summary>
    public static string Render(CycleRecord record)
    {
        var text = new StringBuilder();

        Render(text, record);

        return text.ToString();
    }

    /// <summary>Several, newest first, with a blank line between them.</summary>
    public static string Render(IEnumerable<CycleRecord> records)
    {
        var text = new StringBuilder();

        foreach (var record in records)
        {
            if (text.Length > 0)
            {
                text.AppendLine();
            }

            Render(text, record);
        }

        return text.ToString();
    }

    /// <summary>The one line a list of passes shows before it is expanded.</summary>
    /// <remarks>
    /// The heading of the block below, on its own, because a window listing a
    /// station's recent passes is a list of moments and not a wall of file
    /// names -- the detail is what expanding one is for.
    /// </remarks>
    public static string Heading(CycleRecord record)
    {
        var when = record.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var unit = string.IsNullOrWhiteSpace(record.Unit) ? "(no folder)" : $"\"{record.Unit}\"";
        var cut = record.Completed ? "" : "  cut short";

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{when}  unit {unit}  {Trigger(record.Trigger)}  {record.Seconds:0.0}s{cut}");
    }

    private static void Render(StringBuilder text, CycleRecord record)
    {
        text.AppendLine(Heading(record));

        if (record.Stopped is not null)
        {
            text.Append("  ").AppendLine(record.Stopped);
        }

        text.Append("  ").AppendLine(Walked(record.Folders));

        foreach (var station in record.Stations)
        {
            text.Append("  ").AppendLine(Station(station, Width(record.Stations)));

            if (station.Error is not null)
            {
                text.Append("      ").AppendLine(station.Error);
            }
        }

        if (record.Files.Count == 0)
        {
            return;
        }

        text.AppendLine("  files:");

        foreach (var file in record.Files)
        {
            text.Append("    ").AppendLine(File(file));
        }
    }

    /// <summary>
    /// The folders this pass walked, and how much was in them.
    /// </summary>
    /// <remarks>
    /// A total and then the folders themselves, because on the everyday
    /// station -- one folder -- the total is the whole answer, and on a
    /// station filed by date the list is the thing nothing else records.
    /// </remarks>
    private static string Walked(IReadOnlyList<CycleFolderRecord> folders)
    {
        if (folders.Count == 0)
        {
            return "walked no folders (these station names are built rather than listed)";
        }

        var entries = folders.Sum(folder => folder.Entries);
        var summary = string.Create(
            CultureInfo.CurrentCulture,
            $"walked {Counted(folders.Count, "folder")}, {Counted(entries, "entry", "entries")}");

        return folders.Count == 1
            ? summary
            : summary + ": " + string.Join(
                ", ",
                folders.Select(folder => string.Create(
                    CultureInfo.CurrentCulture, $"{folder.Folder} ({folder.Entries})")));
    }

    private static string Station(CycleStationRecord station, int width)
    {
        var named = string.IsNullOrWhiteSpace(station.Station)
            ? station.StationLinkId.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.CurrentCulture, $"{station.StationLinkId} {station.Station}");

        return string.Create(
            CultureInfo.CurrentCulture,
            $"station {named.PadRight(width)}  scanned {station.Scanned}  held {station.Held}  "
            + $"offered {station.Offered}  wanted {station.Wanted}  up {station.Uploaded}  "
            + $"fail {station.Failed}  backlog {station.Backlog}");
    }

    /// <summary>
    /// How wide the station column is, so the counts line up under each
    /// other.
    /// </summary>
    /// <remarks>
    /// Worth the trouble: the reason these counts are read at all is to spot
    /// the one station in a shared dump directory whose numbers differ from
    /// its neighbours', and a ragged column is a column the eye cannot scan.
    /// </remarks>
    private static int Width(IReadOnlyList<CycleStationRecord> stations) =>
        stations.Count == 0
            ? 0
            : stations.Max(station =>
                station.StationLinkId.ToString(CultureInfo.InvariantCulture).Length
                + (string.IsNullOrWhiteSpace(station.Station) ? 0 : station.Station!.Length + 1));

    private static string File(CycleFileRecord file)
    {
        var mark = file.Outcome switch
        {
            FileOutcomes.Uploaded => "+",
            FileOutcomes.Failed => "x",
            _ => "-",
        };

        // A tally with no name: the remainder of a sample, or the files there
        // was no room to name.
        if (file.Name is null && file.Outcome != FileOutcomes.Failed)
        {
            return string.Create(
                CultureInfo.CurrentCulture, $"… and {file.Count} more {file.Outcome}");
        }

        if (file.Outcome == FileOutcomes.Failed)
        {
            var reason = file.Reason ?? "no reason was recorded";

            if (file.Count == 1)
            {
                return string.Create(
                    CultureInfo.CurrentCulture, $"{mark} {file.Name}{Sized(file.Size)}  failed: {reason}");
            }

            // The example is what makes a folded failure actionable: five
            // hundred files refused for one reason is a sentence, and the one
            // filename beside it is where somebody starts looking.
            var example = file.Name is null
                ? ""
                : string.Create(CultureInfo.CurrentCulture, $" (first: {file.Name})");

            return string.Create(
                CultureInfo.CurrentCulture,
                $"{mark} {Counted(file.Count, "file")} failed: {reason}{example}");
        }

        var why = file.Reason is null ? "" : $" ({file.Reason})";

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{mark} {file.Name}{Sized(file.Size)}  {file.Outcome}{why}");
    }

    /// <summary>
    /// A size a person reads, or nothing when there is none.
    /// </summary>
    /// <remarks>
    /// Binary units and one decimal place, which is what every other tool on
    /// a Windows server shows. The exact byte count is in the JSON for
    /// anybody who wants to add them up.
    /// </remarks>
    private static string Sized(long? size) => size switch
    {
        null => "",
        < 1024 => string.Create(CultureInfo.CurrentCulture, $"  {size} B"),
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"  {size / 1024.0:0.0} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"  {size / (1024.0 * 1024.0):0.0} MB"),
    };

    /// <summary>What started the pass, as a person would say it.</summary>
    private static string Trigger(string trigger) => trigger switch
    {
        CycleTriggers.Scheduled => "scheduled",
        CycleTriggers.Reconciliation => "reconciliation sweep",
        CycleTriggers.Collect => "collect now",
        _ => trigger,
    };

    private static string Counted(int count, string unit, string? plural = null) => count == 1
        ? string.Create(CultureInfo.CurrentCulture, $"1 {unit}")
        : string.Create(CultureInfo.CurrentCulture, $"{count} {plural ?? unit + "s"}");
}
