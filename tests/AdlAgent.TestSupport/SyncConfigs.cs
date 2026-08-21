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
        bool dirStructuredByDate = false) =>
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
                Station = new StationSummary
                {
                    Id = id,
                    Name = $"Station {id}",
                    StationId = $"STATION{id}",
                },
            },
        };
}
