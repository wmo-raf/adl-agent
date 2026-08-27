using AdlAgent.Core.Api;
using AdlAgent.Core.Heartbeat;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Story 23: every machine says what it is and how it is doing, every few
/// minutes.
/// </summary>
public class HeartbeatTests
{
    [Fact]
    public async Task A_beat_carries_what_the_fleet_view_shows()
    {
        await using var agent = new AgentHarness();

        agent.HostLifecycle.PlatformDescription = "Microsoft Windows 10.0.20348";
        agent.HostLifecycle.StartedAt = agent.Time.GetUtcNow() - TimeSpan.FromHours(3);

        agent.Cycles.Record(new CycleUnitReport
        {
            At = agent.Time.GetUtcNow() - TimeSpan.FromMinutes(4),
            Completed = true,
            Links =
            [
                new CycleLinkReport
                {
                    StationLinkId = 11,
                    Scanned = 40,
                    Offered = 6,
                    Uploaded = 5,
                    Failed = 1,
                    Error = "GARISSA_20260821.dat was still being written.",
                },
            ],
            Backlogs = new Dictionary<long, int> { [11] = 2 },
        });

        await agent.PairAsync();
        await agent.HeartbeatLoop.BeatAsync();

        var beat = Assert.Single(agent.Server.Heartbeats);

        Assert.Equal(AgentVersion.Current, beat.AppVersion);
        Assert.Equal("Microsoft Windows 10.0.20348", beat.OsVersion);
        Assert.Equal(3 * 3600, beat.UptimeSeconds);
        Assert.Equal(agent.Time.GetUtcNow(), beat.DeviceTime);
        Assert.Equal(2, beat.BacklogCount);

        var link = Assert.Single(beat.LastCycle!.Links);

        Assert.Equal(11, link.StationLinkId);
        Assert.Equal(40, link.Scanned);
        Assert.Equal(5, link.Uploaded);
        Assert.Equal(1, link.Failed);
        Assert.Contains("still being written", link.Error!);
    }

    [Fact]
    public async Task A_beat_reports_free_space_where_the_watched_folders_are()
    {
        await using var agent = new AgentHarness();

        var folder = Path.GetTempPath();
        var sample = FakeAdlServer.SampleConfig();

        agent.Server.Config = sample with
        {
            Connections =
            [
                sample.Connections[0] with
                {
                    StationLinks =
                    [
                        sample.Connections[0].StationLinks[0] with
                        {
                            Config = sample.Connections[0].StationLinks[0].Config with
                            {
                                LocalFolderPath = folder,
                            },
                        },
                    ],
                },
            ],
        };

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();
        await agent.HeartbeatLoop.BeatAsync();

        var volume = Assert.Single(Assert.Single(agent.Server.Heartbeats).Disk);

        Assert.True(volume.FreeBytes > 0);
        Assert.True(volume.TotalBytes >= volume.FreeBytes);
    }

    [Fact]
    public async Task A_machine_with_nothing_to_report_still_reports()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();
        await agent.HeartbeatLoop.BeatAsync();

        var beat = Assert.Single(agent.Server.Heartbeats);

        // No cycle has finished on a machine that has just been paired, and
        // saying so is not the same as saying zero.
        Assert.Null(beat.LastCycle);
        Assert.Null(beat.BacklogCount);
        Assert.Empty(beat.Disk);
    }

    [Fact]
    public async Task ADL_measures_the_skew_and_the_machine_is_told()
    {
        await using var agent = new AgentHarness();

        // This machine's clock runs two minutes fast.
        agent.Server.ServerTime = agent.Time.GetUtcNow() - TimeSpan.FromMinutes(2);

        await agent.PairAsync();
        await agent.HeartbeatLoop.BeatAsync();

        Assert.Equal(120, agent.Heartbeats.ClockSkewSeconds);
        Assert.Equal("online", agent.Heartbeats.FleetStatus);
    }

    [Fact]
    public async Task An_unreachable_ADL_costs_a_beat_not_the_loop()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();

        agent.Server.Unreachable = true;

        await agent.HeartbeatLoop.BeatAsync();

        Assert.Null(agent.Heartbeats.LastSuccessAt);
        Assert.NotNull(agent.Heartbeats.LastAttemptAt);
        Assert.NotNull(agent.Heartbeats.LastError);

        agent.Server.Unreachable = false;

        await agent.HeartbeatLoop.BeatAsync();

        Assert.NotNull(agent.Heartbeats.LastSuccessAt);
        Assert.Null(agent.Heartbeats.LastError);
    }

    [Fact]
    public async Task The_loop_beats_on_the_cadence_ADL_set()
    {
        await using var agent = new AgentHarness();

        await agent.StartAsync();
        await agent.PairAsync();

        // Pairing wakes the loops, so the technician sees the machine come
        // online rather than waiting out an interval.
        Assert.True(await agent.Server.WaitForHeartbeatsAsync(1));

        await agent.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.True(await agent.Server.WaitForHeartbeatsAsync(2));

        await agent.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.True(await agent.Server.WaitForHeartbeatsAsync(3));
    }

    [Fact]
    public async Task A_fleet_follows_a_cadence_change_without_being_reinstalled()
    {
        await using var agent = new AgentHarness();

        var sample = FakeAdlServer.SampleConfig();

        agent.Server.Config = sample with
        {
            Device = sample.Device with { HeartbeatIntervalMinutes = 20 },
        };

        await agent.StartAsync();
        await agent.PairAsync();

        Assert.True(await agent.Server.WaitForHeartbeatsAsync(1));

        // A beat arriving is not the cadence having been adopted. Two loops
        // can adopt it -- the heartbeat from ADL's answer, the scan cycle
        // from its sync -- and this needs only the first of them, whichever
        // that turns out to be. Which one wins is the scheduler's business,
        // and on a loaded machine it can be neither yet.
        await agent.AtRestAsync();

        Assert.Equal(TimeSpan.FromMinutes(20), agent.Cadence.HeartbeatInterval);

        await agent.AdvanceAsync(TimeSpan.FromMinutes(5));
        await Task.Delay(100);

        Assert.Single(agent.Server.Heartbeats);

        await agent.AdvanceAsync(TimeSpan.FromMinutes(15));

        Assert.True(await agent.Server.WaitForHeartbeatsAsync(2));
    }

    [Fact]
    public async Task The_heartbeat_loop_does_not_wait_on_the_scan_loop()
    {
        await using var agent = new AgentHarness();

        await agent.StartAsync();
        await agent.PairAsync();

        Assert.True(await agent.Server.WaitForHeartbeatsAsync(1));

        // The scan side is wedged: ADL is answering heartbeats and nothing
        // else. A machine in this state must keep reporting, because "up but
        // not working" is precisely the state HQ has never been able to see.
        agent.Server.Config = FakeAdlServer.SampleConfig();

        for (var beats = 2; beats <= 3; beats++)
        {
            await agent.AdvanceAsync(TimeSpan.FromMinutes(5));

            Assert.True(await agent.Server.WaitForHeartbeatsAsync(beats));
        }

        Assert.Null(agent.Heartbeats.LastError);
    }
}
