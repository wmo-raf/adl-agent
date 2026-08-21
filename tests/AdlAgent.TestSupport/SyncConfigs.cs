using AdlAgent.Core.Api;

namespace AdlAgent.TestSupport;

/// <summary>
/// Sync responses, written the way a test wants to read them.
/// </summary>
/// <remarks>
/// A real sync response is four records deep, and a test that arranges one by
/// hand spends more lines saying "one device, one connection" than saying the
/// thing it is actually about. What a cycle test is about is folders,
/// patterns and watermarks, so those are the arguments here and everything
/// else is a default.
/// </remarks>
public static class SyncConfigs
{
    /// <summary>One device, one connection, and the station links given.</summary>
    public static SyncResponse With(params StationLinkConfig[] stationLinks) =>
        With(connectionEnabled: true, stationLinks);

    public static SyncResponse With(bool connectionEnabled, params StationLinkConfig[] stationLinks)
    {
        var sample = FakeAdlServer.SampleConfig();

        return sample with
        {
            Connections =
            [
                sample.Connections[0] with
                {
                    Admin = sample.Connections[0].Admin with { Enabled = connectionEnabled },
                    StationLinks = stationLinks,
                },
            ],
        };
    }

    /// <summary>One station link, bound to a folder and a pattern.</summary>
    public static StationLinkConfig Link(
        long id,
        string folder,
        string pattern = "*",
        DateTimeOffset? watermark = null,
        int stabilityWindowSeconds = 60,
        bool enabled = true,
        string listingStrategy = ListingStrategies.Enumerate,
        bool dirStructuredByDate = false,
        DateTimeOffset? startDate = null) =>
        new()
        {
            Id = id,
            Watermark = watermark,
            Config = new StationLinkAppConfig
            {
                LocalFolderPath = folder,
                FilePattern = pattern,
                StabilityWindowSeconds = stabilityWindowSeconds,
                ListingStrategy = listingStrategy,
                DirStructuredByDate = dirStructuredByDate,
            },
            Admin = new StationLinkAdminConfig
            {
                Enabled = enabled,
                Timezone = "Africa/Nairobi",
                // The watermark unless a test says otherwise, because that
                // is what a real instance sends: ADL derives the watermark
                // from the collection start date and only ever pulls it
                // below, to ask for a pruned file again. A fixture with a
                // watermark and no start date describes a response no ADL
                // produces -- and would have the reconciliation sweep, whose
                // floor is the start date, offer the whole disk.
                StartDate = startDate ?? watermark,
                Station = new StationSummary
                {
                    Id = id,
                    Name = $"Station {id}",
                    StationId = $"STATION{id}",
                },
            },
        };

    /// <summary>
    /// One station link whose filenames are built rather than found.
    /// </summary>
    /// <remarks>
    /// The defaults are a real vendor's convention rather than a minimal one:
    /// a prefix naming the station, a datetime down to the minute, and the
    /// country's own timezone in the name -- which is the setting most likely
    /// to be got wrong, and the one a test that used UTC everywhere would
    /// never exercise.
    /// </remarks>
    public static StationLinkConfig DirectFetchLink(
        long id,
        string folder,
        string prefix = "GARISSA_",
        int? intervalMinutes = 10,
        string? datetimeFormat = "yyyyMMddHHmm",
        string timezone = "Africa/Nairobi",
        string extension = ".dat",
        DateTimeOffset? watermark = null,
        DateTimeOffset? startDate = null,
        int stabilityWindowSeconds = 60,
        bool enabled = true)
    {
        var link = Link(
            id, folder, watermark: watermark, stabilityWindowSeconds: stabilityWindowSeconds,
            enabled: enabled, listingStrategy: ListingStrategies.DirectFetch, startDate: startDate);

        return link with
        {
            Config = link.Config with
            {
                // Blank, as ADL leaves it: a station that builds its
                // filenames has nothing to match them against.
                FilePattern = "",
                DirectFetchPrefix = prefix,
                DirectFetchIntervalMinutes = intervalMinutes,
                DirectFetchDatetimeFormat = datetimeFormat,
                DirectFetchDatetimeTimezone = timezone,
                DirectFetchFileExtension = extension,
            },
        };
    }

    /// <summary>The same device, told how often to reconcile.</summary>
    public static SyncResponse ReconcilingEvery(this SyncResponse config, int? hours) =>
        config with { Device = config.Device with { ReconciliationIntervalHours = hours } };
}
