using System.Globalization;
using System.IO.Compression;
using System.Text;
using AdlAgent.Core.Platform;
using AdlAgent.Core.Status;
using Microsoft.Extensions.Options;

namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// One plain-text file that says everything about this machine, for somebody
/// to email.
/// </summary>
/// <remarks>
/// This is the artefact that actually reaches HQ. A technician in a country
/// nobody can reach is not going to read a JSON Lines file down a telephone,
/// and they are not going to get an operator at HQ into a folder whose
/// permissions are SYSTEM and Administrators. What they can do is press a
/// button and attach what comes out.
/// <para>
/// Written by the agent and not by the tray, which is what makes it work on
/// the service tier: the logs live in a folder the tray's account cannot
/// read, and the service can. The tray asks for a path and the service fills
/// it.
/// </para>
/// <para>
/// Bounded, like everything else here. What goes in is this machine's state,
/// its stations, the most recent unit passes rendered as text, and the tail
/// of the general log -- not the whole ceiling, which is a 96 MB attachment
/// nobody's mail server will take.
/// </para>
/// </remarks>
public sealed class DiagnosticsBundle
{
    /// <summary>How many unit passes the bundle carries.</summary>
    /// <remarks>
    /// A day and a half of an ordinary machine, which is long enough to hold
    /// whatever somebody is writing in about and short enough to read.
    /// </remarks>
    public const int Passes = 200;

    /// <summary>How much of the general log's tail the bundle carries.</summary>
    public const int GeneralLogBytes = 512 * 1024;

    private readonly AgentStatusReader _status;
    private readonly AgentStationsReader _stations;
    private readonly CycleLogReader _passes;
    private readonly IEnumerable<ILogFlush> _logs;
    private readonly string _directory;
    private readonly TimeProvider _time;

    public DiagnosticsBundle(
        AgentStatusReader status,
        AgentStationsReader stations,
        CycleLogReader passes,
        IEnumerable<ILogFlush> logs,
        IOptions<AgentOptions> options,
        IHostLifecycle host,
        TimeProvider time)
    {
        _status = status;
        _stations = stations;
        _passes = passes;
        _logs = logs;
        _directory = AgentLogs.In(options.Value.ResolveStateDirectory(host));
        _time = time;
    }

    /// <summary>Write the bundle to <paramref name="path"/>.</summary>
    /// <remarks>
    /// Both logs are brought up to date first. The pass somebody is writing in
    /// about is very often the one that has just finished, and a bundle read a
    /// moment before the queue caught up would be a bundle missing the only
    /// record anybody wanted.
    /// </remarks>
    /// <returns>How many bytes were written.</returns>
    public async Task<long> WriteToAsync(string path, CancellationToken cancellationToken = default)
    {
        foreach (var log in _logs)
        {
            await log.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var text = Render();

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        await File.WriteAllTextAsync(path, text, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        return new FileInfo(path).Length;
    }

    /// <summary>What a technician saves and sends.</summary>
    public string Render()
    {
        var text = new StringBuilder();
        var status = _status.Read();

        Heading(text, "ADL Agent diagnostics");
        text.AppendLine(Line("Written", _time.GetUtcNow().ToString("u", CultureInfo.InvariantCulture)));
        text.AppendLine(Line("Agent version", status.AgentVersion));
        text.AppendLine(Line("ADL", status.AdlUrl));
        text.AppendLine(Line("ADL version", status.AdlVersion));
        text.AppendLine(Line("Device", $"{status.DeviceName} ({status.DeviceId})"));
        text.AppendLine(Line("Pairing", status.PairingState));
        text.AppendLine(Line("Last synced", Moment(status.LastSyncedAt)));
        text.AppendLine(Line("Configuration", status.ConfigVersion?.ToString(CultureInfo.InvariantCulture) ?? "-"));
        text.AppendLine(Line("From cache", status.ConfigFromCache ? "yes" : "no"));
        text.AppendLine(Line("Last heartbeat", Moment(status.LastHeartbeatAt)));
        text.AppendLine(Line("ADL says", status.FleetStatus ?? "-"));
        text.AppendLine(Line("Clock difference", status.ClockSkewSeconds is { } skew
            ? string.Create(CultureInfo.InvariantCulture, $"{skew}s")
            : "-"));
        text.AppendLine(Line("Updates", $"{status.UpdateState} {status.UpdateDetail}".Trim()));
        text.AppendLine(Line("Last problem", status.LastError ?? "-"));

        Stations(text);
        Passes_(text);
        General(text);

        return text.ToString();
    }

    private void Stations(StringBuilder text)
    {
        Heading(text, "Stations");

        var stations = _stations.Read();

        if (stations.Stations.Count == 0)
        {
            text.AppendLine("This machine has no stations linked to it.");

            return;
        }

        foreach (var station in stations.Stations)
        {
            text.AppendLine(string.Create(
                CultureInfo.CurrentCulture,
                $"{station.StationLinkId}  {station.StationName}  ({station.ConnectionName})"));
            text.AppendLine(Line("  folder", station.Config.LocalFolderPath));
            text.AppendLine(Line("  pattern", station.Config.FilePattern ?? "-"));
            text.AppendLine(Line("  strategy", station.Config.ListingStrategy));
            text.AppendLine(Line("  enabled", station.Enabled ? "yes" : "no"));
            text.AppendLine(Line("  wants files from", Moment(station.Watermark)));
            text.AppendLine(Line("  ADL last received", Moment(station.LastReceivedAt)));

            if (station.Error is not null)
            {
                text.AppendLine(Line("  problem", station.Error));
            }
        }
    }

    /// <summary>
    /// The recent unit passes, rendered exactly as the window renders them.
    /// </summary>
    /// <remarks>
    /// One renderer, so that the pass a technician read on screen before
    /// pressing the button and the pass HQ reads in the attachment are the
    /// same sentences. A support conversation where the two ends are looking
    /// at differently-worded copies of one event is a conversation about the
    /// wording.
    /// </remarks>
    private void Passes_(StringBuilder text)
    {
        Heading(text, $"Recent collection passes (newest first, at most {Passes})");

        var records = _passes.Recent(Passes);

        text.AppendLine(records.Count == 0
            ? "This machine has not recorded a collection pass yet."
            : CycleRecordText.Render(records));
    }

    /// <summary>The tail of the general log.</summary>
    /// <remarks>
    /// The tail and not the whole, because the whole is up to the general
    /// log's entire ceiling and this has to survive being attached to an
    /// email from a country server. The newest of it is also the part
    /// anybody reads.
    /// </remarks>
    private void General(StringBuilder text)
    {
        Heading(text, $"Agent log (the last {GeneralLogBytes / 1024} KB)");

        try
        {
            var newest = Directory.Exists(_directory)
                ? Directory
                    .EnumerateFiles(
                        _directory,
                        $"{AgentLogs.GeneralLogName}-*{AgentFileLoggerProvider.Extension}*")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;

            if (newest is null)
            {
                text.AppendLine("There is no agent log on this machine yet.");

                return;
            }

            text.AppendLine(Tail(newest, GeneralLogBytes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            text.AppendLine($"The agent log could not be read: {exception.Message}");
        }
    }

    /// <summary>The last <paramref name="bytes"/> of a log file, plain or gzipped.</summary>
    private static string Tail(string path, int bytes)
    {
        using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (path.EndsWith(".gz", StringComparison.Ordinal))
        {
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var whole = reader.ReadToEnd();

            return whole.Length <= bytes ? whole : whole[^bytes..];
        }

        if (file.Length > bytes)
        {
            file.Seek(-bytes, SeekOrigin.End);
        }

        using var plain = new StreamReader(file, Encoding.UTF8);

        return plain.ReadToEnd();
    }

    private static void Heading(StringBuilder text, string title)
    {
        if (text.Length > 0)
        {
            text.AppendLine();
        }

        text.AppendLine(title);
        text.AppendLine(new string('=', title.Length));
    }

    private static string Line(string field, string value) => $"{field,-22}{value}";

    private static string Moment(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "-";
}
