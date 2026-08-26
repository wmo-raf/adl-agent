using System.Text.Json;
using AdlAgent.Core.Api;
using AdlAgent.Core.Serialization;
using AdlAgent.Core.State;
using AdlAgent.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The correctness backstop: anything in the folder that ADL lacks is
/// eventually offered, however it got there.
/// </summary>
/// <remarks>
/// An ordinary cycle offers what is at or after ADL's watermark for a
/// station, and that is a bet -- on creation times, on vendors' clocks, on
/// nobody moving a file in with an old date on it. The bet is worth making
/// because it is what keeps a settled folder cheap, and it is only safe to
/// make because of what is tested here: once a day the station stops betting
/// and offers the whole folder back to its collection start date, and the
/// ledger diff on ADL's side decides what that was worth.
/// </remarks>
public class ReconciliationTests
{
    private const string Folder = "C:\\VendorData\\Garissa";

    [Fact]
    public async Task An_old_file_smuggled_into_the_folder_is_found_by_the_sweep()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Link(agent));

        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // A file recovered off a dead logger and copied in by hand, carrying
        // the date it was written. On a filesystem with no creation time --
        // or with a vendor's archiving job that preserves both timestamps --
        // it looks a week old to the candidate window, and the window is
        // right to leave it alone: that is what makes the window cheap.
        agent.Files.Add(Folder, "GARISSA_20260814.dat", Recovered(agent), "a week ago\n");

        await agent.Cycle.RunAsync();

        Assert.Null(agent.Server.Held(11, "GARISSA_20260814.dat"));

        // A day later the station stops betting and offers everything.
        agent.Time.Advance(TimeSpan.FromHours(25));

        await agent.Cycle.RunAsync();

        Assert.Equal("a week ago\n", agent.Server.Held(11, "GARISSA_20260814.dat")!.Text);
    }

    [Fact]
    public async Task The_sweep_does_not_reach_below_the_collection_start_date()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Link(agent));

        // Older than the date an administrator said collection begins. The
        // sweep is a lower floor, not the absence of one: a station whose
        // start date was moved forward to skip a bad year must not have that
        // year offered back to it every night.
        agent.Files
            .Add(Folder, "GARISSA_20260821.dat", Settled(agent), "today\n")
            .Add(Folder, "GARISSA_20250101.dat", agent.Time.GetUtcNow() - TimeSpan.FromDays(60), "long ago\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Null(agent.Server.Held(11, "GARISSA_20250101.dat"));
        Assert.Equal("today\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
    }

    [Fact]
    public async Task The_sweep_never_offers_less_than_an_ordinary_cycle_would()
    {
        await using var agent = new AgentHarness();

        // ADL pulls a link's watermark below its collection start date on
        // purpose, when an operator asks for a file whose staged bytes were
        // pruned to be sent again. A sweep that read the start date alone
        // would raise the floor back over the very file that was asked for.
        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(
                11,
                Folder,
                "*.dat",
                watermark: agent.Time.GetUtcNow() - TimeSpan.FromDays(90),
                startDate: agent.Time.GetUtcNow() - TimeSpan.FromDays(30)));

        agent.Files.Add(
            Folder, "GARISSA_20260601.dat", agent.Time.GetUtcNow() - TimeSpan.FromDays(60), "pruned\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal("pruned\n", agent.Server.Held(11, "GARISSA_20260601.dat")!.Text);
    }

    [Fact]
    public async Task A_sweep_is_a_lower_floor_and_not_a_second_walk()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Link(agent));

        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        agent.Time.Advance(TimeSpan.FromHours(25));

        await agent.Cycle.RunAsync();

        // Two cycles, two walks. A sweep that walked the folder a second time
        // to see it a second way would double the cost of the one thing that
        // is already the expensive part.
        Assert.Equal(2, agent.Files.EnumerationsOf(Folder));

        // And ADL was not sent the file again: it holds it, so it asks for
        // nothing.
        Assert.Single(agent.Server.RequestsFor("files/"));
    }

    [Fact]
    public async Task ADL_says_how_often_to_reconcile()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Link(agent)).ReconcilingEvery(1);

        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        agent.Files.Add(Folder, "GARISSA_20260814.dat", Recovered(agent), "a week ago\n");

        // Half an hour on: an instance that reconciles hourly has not come
        // round yet.
        agent.Time.Advance(TimeSpan.FromMinutes(30));

        await agent.Cycle.RunAsync();

        Assert.Null(agent.Server.Held(11, "GARISSA_20260814.dat"));

        agent.Time.Advance(TimeSpan.FromMinutes(31));

        await agent.Cycle.RunAsync();

        Assert.Equal("a week ago\n", agent.Server.Held(11, "GARISSA_20260814.dat")!.Text);
    }

    [Fact]
    public async Task An_instance_that_cannot_afford_sweeps_can_switch_them_off()
    {
        await using var agent = new AgentHarness();

        // A real choice, for an instance whose country links cannot carry a
        // full folder's manifest. Obeyed rather than clamped to something
        // safer: a deployment that turns this off has decided what it is
        // giving up.
        agent.Server.Config = SyncConfigs.With(Link(agent)).ReconcilingEvery(0);

        agent.Files
            .Add(Folder, "GARISSA_20260821.dat", Settled(agent), "today\n")
            .Add(Folder, "GARISSA_20260814.dat", Recovered(agent), "a week ago\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        agent.Time.Advance(TimeSpan.FromDays(3));

        await agent.Cycle.RunAsync();

        Assert.Equal("today\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
        Assert.Null(agent.Server.Held(11, "GARISSA_20260814.dat"));
        Assert.Equal(0, agent.Store.SweepWrites);
    }

    [Fact]
    public async Task A_sweep_cut_short_by_an_unreachable_ADL_is_still_owed()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Link(agent));

        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        agent.Files.Add(Folder, "GARISSA_20260814.dat", Recovered(agent), "a week ago\n");

        agent.Time.Advance(TimeSpan.FromHours(25));

        // The link drops in the middle of the sweep. Half a folder was
        // offered and the rest was not, and recording that as done would
        // leave the unoffered half waiting another day for no reason.
        agent.Server.Unreachable = true;

        await agent.Cycle.RunAsync();

        agent.Server.Unreachable = false;

        // The very next cycle, with no time passing at all.
        await agent.Cycle.RunAsync();

        Assert.Equal("a week ago\n", agent.Server.Held(11, "GARISSA_20260814.dat")!.Text);
    }

    [Fact]
    public async Task A_station_the_scan_turned_away_has_not_been_reconciled()
    {
        await using var agent = new AgentHarness();

        // No file pattern, so the scan will not touch this station at all: a
        // folder is nearly always shared, and guessing "every file" would
        // file one station's data under another.
        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(
                11,
                Folder,
                pattern: "",
                watermark: agent.Time.GetUtcNow() - TimeSpan.FromHours(1),
                startDate: agent.Time.GetUtcNow() - TimeSpan.FromDays(30)));

        agent.Files
            .Add(Folder, "GARISSA_20260821.dat", Settled(agent), "today\n")
            .Add(Folder, "GARISSA_20260814.dat", Recovered(agent), "a week ago\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Contains("No file pattern", agent.Cycles.LastCompletedCycle!.Links.Single().Error!);
        Assert.Equal(0, agent.Store.SweepWrites);

        // A technician sets the pattern a minute later. The station has to be
        // reconciled on the cycle after that -- not a day after it, because a
        // cycle that never looked at the folder had its day's reconciliation
        // stamped on it.
        agent.Server.Config = SyncConfigs.With(Link(agent));

        await agent.Cycle.RunAsync();

        Assert.Equal("a week ago\n", agent.Server.Held(11, "GARISSA_20260814.dat")!.Text);
    }

    [Fact]
    public async Task A_restarted_service_does_not_sweep_again_because_it_restarted()
    {
        var store = new InMemoryAgentStateStore();

        await using (var agent = new AgentHarness(store))
        {
            agent.Server.Config = SyncConfigs.With(Link(agent));
            agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "today\n");

            await agent.PairAsync();
            await agent.Cycle.RunAsync();
        }

        // The service restarts, as it does on every crash, every reboot and
        // every auto-update. A machine that swept because it had restarted
        // would offer its whole folder each time -- two hundred manifest
        // pages on the folders this product exists for, down a link that is
        // slow on its good days.
        await using var restarted = new AgentHarness(store);

        restarted.Server.Config = SyncConfigs.With(Link(restarted));

        restarted.Files
            .Add(Folder, "GARISSA_20260821.dat", Settled(restarted), "today\n")
            .Add(Folder, "GARISSA_20260814.dat", Recovered(restarted), "a week ago\n");

        await restarted.Cycle.RunAsync();

        Assert.Null(restarted.Server.Held(11, "GARISSA_20260814.dat"));

        // And the day still comes round.
        restarted.Time.Advance(TimeSpan.FromHours(25));

        await restarted.Cycle.RunAsync();

        Assert.Equal("a week ago\n", restarted.Server.Held(11, "GARISSA_20260814.dat")!.Text);
    }

    [Fact]
    public async Task The_sweep_log_is_written_when_a_station_is_swept_and_not_otherwise()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Link(agent));

        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal(1, agent.Store.SweepWrites);

        // Six hours of ordinary cycles. Flushing a file every check interval
        // to say nothing had changed would be a write every ten minutes for
        // the life of the install.
        for (var cycle = 0; cycle < 3; cycle++)
        {
            agent.Time.Advance(TimeSpan.FromHours(2));

            await agent.Cycle.RunAsync();
        }

        Assert.Equal(1, agent.Store.SweepWrites);

        agent.Time.Advance(TimeSpan.FromHours(20));

        await agent.Cycle.RunAsync();

        Assert.Equal(2, agent.Store.SweepWrites);
    }

    [Fact]
    public void The_sweep_log_survives_the_disk_it_is_written_to()
    {
        var directory = Directory.CreateTempSubdirectory("adl-agent-sweeps").FullName;

        try
        {
            var swept = new Dictionary<long, DateTimeOffset>
            {
                [11] = DateTimeOffset.Parse("2026-08-21T09:00:00Z"),
                [12] = DateTimeOffset.Parse("2026-08-20T23:30:00Z"),
            };

            Store(directory).SaveSweeps(new SweepLog { Swept = swept });

            // A second store over the same directory is what a restarted
            // service has. Written out here as well as in memory because the
            // station link id is a number used as a JSON object key, which is
            // the sort of thing that round-trips until one day it does not --
            // and the whole failure would be a fleet quietly offering every
            // folder it has, every time a machine reboots.
            Assert.Equal(swept, Store(directory).LoadSweeps().Swept);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static FileAgentStateStore Store(string directory) =>
        new(
            Options.Create(new Core.AgentOptions
            {
                AdlBaseUrl = "https://adl.example.org",
                StateDirectory = directory,
            }),
            new FakeHostLifecycle(),
            NullLogger<FileAgentStateStore>.Instance);

    /// <summary>
    /// One station whose watermark sits well above its collection start date.
    /// </summary>
    /// <remarks>
    /// The gap between the two is where every one of these tests lives: it is
    /// what an ordinary cycle declines to look at and what a sweep goes back
    /// over.
    /// </remarks>
    private static Core.Api.StationLinkConfig Link(AgentHarness agent) =>
        SyncConfigs.Link(
            11,
            Folder,
            "*.dat",
            watermark: agent.Time.GetUtcNow() - TimeSpan.FromHours(1),
            startDate: agent.Time.GetUtcNow() - TimeSpan.FromDays(30));

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);

    /// <summary>A file whose timestamps put it a week back, however it arrived.</summary>
    private static DateTimeOffset Recovered(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromDays(7);

    // ---------- the cadence ADL states, in both places it states it ----------
    //
    // Literal bodies rather than round trips through this agent's own
    // serializer, which would prove only that it agrees with itself. ADL and
    // the agent are separate repositories on separate release trains, and a
    // field renamed on the plugin side would otherwise surface as a fleet
    // quietly reconciling daily against an instance that asked it not to.

    [Fact]
    public void The_interval_ADL_sends_in_a_sync_is_the_one_this_agent_reads()
    {
        const string body = """
            {
              "id": 3,
              "name": "Songea server",
              "check_interval_minutes": 5,
              "heartbeat_interval_minutes": 5,
              "reconciliation_interval_hours": 168
            }
            """;

        var device = JsonSerializer.Deserialize<DeviceConfig>(body, AgentJson.Options)!;

        Assert.Equal(168, device.ReconciliationIntervalHours);
    }

    [Fact]
    public void The_interval_rides_the_heartbeat_under_the_same_name()
    {
        const string body = """
            {
              "device_id": 3,
              "status": "online",
              "heartbeat_interval_minutes": 5,
              "check_interval_minutes": 5,
              "reconciliation_interval_hours": 0,
              "config_version": 6
            }
            """;

        var response = JsonSerializer.Deserialize<HeartbeatResponse>(body, AgentJson.Options)!;

        // Zero, and still zero: a deployment that has switched sweeps off is
        // the one case where the number has to survive being read as itself
        // rather than as an absent field.
        Assert.Equal(0, response.ReconciliationIntervalHours);
    }

    [Fact]
    public void An_ADL_that_predates_the_setting_leaves_the_beat_silent_about_it()
    {
        const string body = """
            {
              "device_id": 3,
              "status": "online",
              "heartbeat_interval_minutes": 5,
              "check_interval_minutes": 5,
              "config_version": 6
            }
            """;

        var response = JsonSerializer.Deserialize<HeartbeatResponse>(body, AgentJson.Options)!;

        // Null and not zero. The two are opposite instructions, and an older
        // ADL saying nothing must not read as one asking for no sweeps.
        Assert.Null(response.ReconciliationIntervalHours);
    }
}
