using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Story 11: fetch the configuration every cycle, and keep working when ADL
/// cannot be reached.
/// </summary>
public class ConfigurationCacheTests
{
    [Fact]
    public async Task A_sync_brings_back_the_whole_world_this_device_works_from()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();

        var configuration = await agent.Configuration.RefreshAsync();

        Assert.NotNull(configuration);
        Assert.False(configuration.FromCache);
        Assert.Equal(1, configuration.Version);

        var link = Assert.Single(configuration.StationLinks);

        Assert.Equal("C:\\VendorData\\Garissa", link.Config.LocalFolderPath);
        Assert.Equal("GARISSA_*.dat", link.Config.FilePattern);
        Assert.Equal(TimeSpan.FromSeconds(60), link.Config.StabilityWindow);

        // The admin tier arrives too, so the app can show what HQ decided
        // without being able to change it.
        Assert.Equal("Garissa", link.Admin.Station.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T00:00:00Z"), link.Watermark);
    }

    [Fact]
    public async Task An_unreachable_ADL_is_answered_from_the_cache()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        agent.Server.Unreachable = true;

        var configuration = await agent.Configuration.RefreshAsync();

        Assert.NotNull(configuration);
        Assert.True(configuration.FromCache);

        // The same folders as before the link went down: an outage costs the
        // agent its news, not its work.
        Assert.Equal(
            "C:\\VendorData\\Garissa",
            Assert.Single(configuration.StationLinks).Config.LocalFolderPath);
    }

    [Fact]
    public async Task A_restarted_machine_works_from_the_cache_before_it_reaches_ADL()
    {
        await using var agent = new AgentHarness();

        // What the previous run left on disk.
        agent.Store.Seed(FakeAdlServer.SampleConfig(), DateTimeOffset.Parse("2026-08-20T22:00:00Z"));

        var configuration = agent.Configuration.Current;

        Assert.NotNull(configuration);
        Assert.True(configuration.FromCache);
        Assert.Equal(DateTimeOffset.Parse("2026-08-20T22:00:00Z"), configuration.FetchedAt);

        // Nothing was asked of ADL to answer that.
        Assert.Empty(agent.Server.Requests);
    }

    [Fact]
    public async Task A_machine_that_has_never_synced_and_cannot_reach_ADL_has_nothing_to_work_from()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();

        agent.Server.Unreachable = true;

        Assert.Null(await agent.Configuration.RefreshAsync());
    }

    [Fact]
    public async Task A_configuration_changed_in_ADL_reaches_the_machine_on_the_next_sync()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var moved = FakeAdlServer.SampleConfig();

        agent.Server.Config = moved with
        {
            ConfigVersion = 2,
            Connections =
            [
                moved.Connections[0] with
                {
                    StationLinks =
                    [
                        moved.Connections[0].StationLinks[0] with
                        {
                            Config = moved.Connections[0].StationLinks[0].Config with
                            {
                                LocalFolderPath = "D:\\NewVendorData\\Garissa",
                            },
                        },
                    ],
                },
            ],
        };

        var configuration = await agent.Configuration.RefreshAsync();

        Assert.NotNull(configuration);
        Assert.Equal(2, configuration.Version);
        Assert.Equal(
            "D:\\NewVendorData\\Garissa",
            Assert.Single(configuration.StationLinks).Config.LocalFolderPath);
    }

    [Fact]
    public async Task The_cadences_are_ADLs_to_set()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = FakeAdlServer.SampleConfig() with
        {
            Device = FakeAdlServer.SampleConfig().Device with
            {
                CheckIntervalMinutes = 30,
                HeartbeatIntervalMinutes = 15,
            },
        };

        await agent.PairAsync();
        await agent.SyncLoop.SyncAsync();

        Assert.Equal(TimeSpan.FromMinutes(15), agent.Cadence.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromMinutes(30), agent.Cadence.CheckInterval);
    }

    [Fact]
    public async Task A_cadence_that_would_hammer_ADL_is_ignored()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = FakeAdlServer.SampleConfig() with
        {
            Device = FakeAdlServer.SampleConfig().Device with
            {
                CheckIntervalMinutes = 0,
                HeartbeatIntervalMinutes = 0,
            },
        };

        await agent.PairAsync();
        await agent.SyncLoop.SyncAsync();

        Assert.Equal(AgentCadenceDefaults.Heartbeat, agent.Cadence.HeartbeatInterval);
        Assert.Equal(AgentCadenceDefaults.Check, agent.Cadence.CheckInterval);
    }
}

/// <summary>The intervals a machine falls back on when ADL's are unusable.</summary>
internal static class AgentCadenceDefaults
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Check = TimeSpan.FromMinutes(10);
}
