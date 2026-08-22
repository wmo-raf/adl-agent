using AdlAgent.Core.Api;
using AdlAgent.Core.Configuration;
using AdlAgent.Core.Heartbeat;

namespace AdlAgent.Core.Status;

/// <summary>
/// Puts the station list in front of the tray: ADL's linking, this machine's
/// bindings, and the last cycle's counts, joined.
/// </summary>
/// <remarks>
/// The join is the whole of this class, and it is worth having somewhere
/// rather than in the tray. The two halves come from different places and
/// move at different rates -- the configuration from a sync, the counts from
/// a cycle that may have run against an older one -- and a station that
/// exists in one and not the other is the ordinary case just after HQ links
/// a new station. Doing it here means the tray, the Linux CLI and anything
/// after them all get the same answer to "what about the station ADL has
/// only just told us about".
/// </remarks>
public sealed class AgentStationsReader
{
    private readonly ConfigurationService _configuration;
    private readonly ICycleReportSource _cycles;

    public AgentStationsReader(ConfigurationService configuration, ICycleReportSource cycles)
    {
        _configuration = configuration;
        _cycles = cycles;
    }

    public AgentStationsSnapshot Read()
    {
        var snapshot = _configuration.Snapshot();
        var cycle = _cycles.LastCompletedCycle;

        // Keyed rather than searched per station: a device may serve forty
        // stations and this is drawn every time the window opens.
        var counts = cycle?.Links.ToDictionary(link => link.StationLinkId)
            ?? [];

        var stations = new List<AgentStationSnapshot>();

        foreach (var connection in snapshot.Configuration?.Sync.Connections ?? [])
        {
            foreach (var link in connection.StationLinks)
            {
                counts.TryGetValue(link.Id, out var last);

                stations.Add(new AgentStationSnapshot
                {
                    StationLinkId = link.Id,
                    ConnectionId = connection.Id,
                    ConnectionName = connection.Name,
                    StationName = link.Admin.Station.Name,
                    StationId = link.Admin.Station.StationId,
                    WigosId = link.Admin.Station.WigosId,
                    // Both, because a station under a switched-off connection
                    // is switched off however its own flag reads.
                    Enabled = connection.Admin.Enabled && link.Admin.Enabled,
                    Watermark = link.Watermark,
                    StartDate = link.Admin.StartDate,
                    Timezone = link.Admin.Timezone,
                    Config = link.Config,
                    Scanned = last?.Scanned,
                    Offered = last?.Offered,
                    Uploaded = last?.Uploaded,
                    Failed = last?.Failed,
                    Error = last?.Error,
                });
            }
        }

        return new AgentStationsSnapshot
        {
            Stations = stations,
            LastSyncedAt = snapshot.LastSyncedAt,
            ConfigFromCache = snapshot.Configuration?.FromCache ?? false,
            ConfigVersion = snapshot.Configuration?.Version,
            LastCycleAt = cycle?.CompletedAt,
        };
    }
}
