using AdlAgent.Core.Api;
using AdlAgent.Core.Configuration;
using AdlAgent.Core.Cycle;
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
/// <para>
/// What it flattens is the stations. The connections above them are passed
/// through as a list of their own, because two things about a connection
/// survive nowhere else: whether ADL has switched the whole thing off, which
/// otherwise reaches the machine only as a false on each of its stations; and
/// the existence of a connection that has no station links yet, which
/// otherwise reaches it not at all.
/// </para>
/// </remarks>
public sealed class AgentStationsReader
{
    private readonly ConfigurationService _configuration;
    private readonly ICycleReportSource _cycles;
    private readonly OnDemandCollect _requested;
    private readonly UploadCycle _cycle;
    private readonly TimeProvider _time;

    public AgentStationsReader(
        ConfigurationService configuration,
        ICycleReportSource cycles,
        OnDemandCollect requested,
        UploadCycle cycle,
        TimeProvider time)
    {
        _configuration = configuration;
        _cycles = cycles;
        _requested = requested;
        _cycle = cycle;
        _time = time;
    }

    public AgentStationsSnapshot Read()
    {
        var snapshot = _configuration.Snapshot();
        var cycle = _cycles.LastCompletedCycle;

        // Read once for the whole walk rather than per station, so that forty
        // rows built in one pass are judged against one instant. Two stations
        // either side of the same window ought not to disagree because the
        // clock moved between them.
        var now = _time.GetUtcNow();

        // Keyed rather than searched per station: a device may serve forty
        // stations and this is drawn every time the window opens.
        var counts = cycle?.Links.ToDictionary(link => link.StationLinkId)
            ?? [];

        var stations = new List<AgentStationSnapshot>();
        var connections = new List<AgentConnectionSnapshot>();

        foreach (var connection in snapshot.Configuration?.Sync.Connections ?? [])
        {
            // Before its stations, and unconditionally. A connection with no
            // station links is the ordinary state of one an administrator has
            // just made, and it has to reach the machine as a connection with
            // nothing in it rather than as nothing at all.
            connections.Add(new AgentConnectionSnapshot
            {
                ConnectionId = connection.Id,
                ConnectionName = connection.Name,
                Network = connection.Admin.Network,
                Enabled = connection.Admin.Enabled,
                StaleAfterMinutes = connection.Admin.StaleAfterMinutes,
            });

            foreach (var link in connection.StationLinks)
            {
                counts.TryGetValue(link.Id, out var last);

                // Both flags, for the same reason Enabled below folds them:
                // a station under a switched-off connection is switched off
                // however its own flag reads, and a verdict that said
                // otherwise would put an amber dot on a row an administrator
                // deliberately silenced.
                var enabled = connection.Admin.Enabled && link.Admin.Enabled;

                stations.Add(new AgentStationSnapshot
                {
                    StationLinkId = link.Id,
                    ConnectionId = connection.Id,
                    ConnectionName = connection.Name,
                    StationName = link.Admin.Station.Name,
                    StationId = link.Admin.Station.StationId,
                    WigosId = link.Admin.Station.WigosId,
                    Enabled = enabled,
                    Collecting = _cycle.IsCollecting(link.Id),
                    Watermark = link.Watermark,
                    LastReceivedAt = link.LastReceivedAt,
                    // The connection's window resolved onto its station, so
                    // that everything downstream can judge a flat list. The
                    // machine-wide questions -- what the line at the top of
                    // the window says, which connection it opens on -- span
                    // connections that may state different windows, and
                    // re-finding each station's connection to answer them
                    // would be this walk done a second time.
                    Flow = StationFlows.Of(
                        enabled,
                        link.Config.LocalFolderPath,
                        last?.Error,
                        link.LastReceivedAt,
                        connection.Admin.StaleAfterMinutes,
                        now),
                    StartDate = link.Admin.StartDate,
                    Timezone = link.Admin.Timezone,
                    Config = link.Config,
                    Scanned = last?.Scanned,
                    Offered = last?.Offered,
                    Uploaded = last?.Uploaded,
                    Failed = last?.Failed,
                    Error = last?.Error,
                    // Null once a scheduled pass has overtaken it, which is
                    // the whole of the join: the row shows one age of one
                    // fact, and which one it is decided here rather than in
                    // each UI.
                    //
                    // Against this station's own last pass, not the machine's.
                    // Collection runs a unit at a time, so the machine's most
                    // recent finish is some other folder's and says nothing
                    // about whether this station has been round again since
                    // the button was pressed.
                    Requested = _requested.For(link.Id, _cycles.LastPassAt(link.Id)),
                });
            }
        }

        return new AgentStationsSnapshot
        {
            Connections = connections,
            Stations = stations,
            AsOf = now,
            LastSyncedAt = snapshot.LastSyncedAt,
            ConfigFromCache = snapshot.Configuration?.FromCache ?? false,
            ConfigVersion = snapshot.Configuration?.Version,
            LastCycleAt = cycle?.CompletedAt,
        };
    }
}
