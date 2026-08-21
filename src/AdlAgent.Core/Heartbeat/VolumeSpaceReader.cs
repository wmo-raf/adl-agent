using AdlAgent.Core.Api;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Heartbeat;

/// <summary>
/// Free space on the volumes this machine's watched folders sit on.
/// </summary>
/// <remarks>
/// Reported because the failure it predicts is invisible from ADL: a country
/// server whose disk fills stops being able to write the very files it is
/// meant to be sending, and nothing about that arrives as an error --
/// the folder simply stops changing.
/// <para>
/// Not a platform seam, though it looks like one: a drive's free bytes is a
/// question the base library answers the same way everywhere. What it is
/// instead is best-effort -- an unreadable volume is left out of the report
/// rather than costing the beat.
/// </para>
/// </remarks>
public sealed class VolumeSpaceReader
{
    private readonly ILogger<VolumeSpaceReader> _logger;

    public VolumeSpaceReader(ILogger<VolumeSpaceReader> logger)
    {
        _logger = logger;
    }

    /// <summary>One entry per distinct volume behind <paramref name="folderPaths"/>.</summary>
    public IReadOnlyList<VolumeReport> Read(IEnumerable<string> folderPaths)
    {
        var reports = new List<VolumeReport>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folderPaths)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            var report = Describe(folder);

            if (report is not null && seen.Add(report.Volume))
            {
                reports.Add(report);
            }
        }

        return reports;
    }

    private VolumeReport? Describe(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));

            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);

            if (!drive.IsReady)
            {
                return null;
            }

            return new VolumeReport
            {
                Volume = drive.Name,
                FreeBytes = drive.AvailableFreeSpace,
                TotalBytes = drive.TotalSize,
            };
        }
        catch (Exception exception) when (
            exception is IOException or ArgumentException or UnauthorizedAccessException
                or NotSupportedException)
        {
            // A folder on a disconnected share, or a path this OS cannot
            // parse (a Windows path read from a cache on a test runner).
            // Neither is worth a beat.
            _logger.LogDebug(exception, "Could not read free space for {Folder}.", folder);

            return null;
        }
    }
}
