using System.Text.Json.Nodes;
using AdlAgent.Core.Api;
using AdlAgent.TestSupport;
using AdlAgent.Windows.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The conversation a technician's window has with the service, end to end
/// over the real transport.
/// </summary>
/// <remarks>
/// The WPF window itself is not automated -- the spec says so, and it holds
/// nothing worth automating -- but everything underneath it is: the pipe, the
/// protocol, the commands, and the typed answers the window binds to. What is
/// left unautomated above this line is layout.
/// <para>
/// Each test serves on a pipe name of its own, so the suite neither collides
/// with itself nor cares whether this machine has a real agent installed.
/// </para>
/// </remarks>
public class LocalUiTests
{
    private const string Folder = "C:\\VendorData\\Garissa";

    // Short: a pipe name becomes a unix socket path off Windows, and that
    // path has 104 characters to play with including the temp directory.
    private readonly string _pipeName = $"adl-u{Guid.NewGuid():N}"[..13];

    [Fact]
    public async Task The_window_reads_the_machines_standing_over_the_pipe()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServeAsync(agent);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var status = await serving.Link.StatusAsync();

        Assert.True(status.Ok);
        Assert.Equal("Paired", status.Value!.PairingState);
        Assert.Equal(agent.Server.Device.Name, status.Value.DeviceName);
        Assert.Equal(1, status.Value.StationLinkCount);
        Assert.False(status.Value.RePairNeeded);
    }

    [Fact]
    public async Task A_service_that_is_not_running_is_a_sentence_rather_than_an_exception()
    {
        // Nothing is serving this pipe. It is the commonest thing to be
        // wrong on a fresh install, and the window must draw it rather than
        // fall over.
        var link = new AgentControlLink(
            () => new NamedPipeControlClient(TimeSpan.FromMilliseconds(200), _pipeName));

        var status = await link.StatusAsync();

        Assert.False(status.Ok);
        Assert.False(status.ServiceReached);
        Assert.Contains("not answering", status.Detail);
    }

    [Fact]
    public async Task The_station_list_arrives_with_each_stations_local_binding()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServeAsync(agent);

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, Folder, "GARISSA_*.dat"),
            SyncConfigs.Link(12, "C:\\VendorData\\Mombasa", "MOMBASA_*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var stations = await serving.Link.StationsAsync();

        Assert.True(stations.Ok);
        Assert.Equal(2, stations.Value!.Stations.Count);
        Assert.Equal(Folder, stations.Value.Stations[0].Config.LocalFolderPath);
        Assert.Equal("Vaisala AWS", stations.Value.Stations[0].ConnectionName);
        Assert.True(stations.Value.Stations[0].Enabled);
    }

    [Fact]
    public async Task A_pattern_being_typed_is_counted_against_the_folder_while_it_is_typed()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServeAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        agent.Files
            .Add(Folder, "GARISSA_20260821.dat", Settled(agent))
            .Add(Folder, "MOMBASA_20260821.dat", Settled(agent));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        // What the window sends on each keystroke: the station, and the one
        // box being edited.
        var narrow = await serving.Link.PreviewAsync(new JsonObject
        {
            ["station_link_id"] = 11,
            ["file_pattern"] = "GARISSA_*",
        });

        var wide = await serving.Link.PreviewAsync(new JsonObject
        {
            ["station_link_id"] = 11,
            ["file_pattern"] = "*.dat",
        });

        Assert.Equal(1, narrow.Value!.Matches);
        Assert.Equal(2, wide.Value!.Matches);
        Assert.Equal(Folder, wide.Value.LocalFolderPath);
    }

    [Fact]
    public async Task A_folder_bound_in_the_window_is_written_through_to_ADL()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServeAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, folder: ""));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var written = await serving.Link.ConfigureAsync(11, new JsonObject
        {
            ["local_folder_path"] = Folder,
            ["file_pattern"] = "GARISSA_*.dat",
        });

        Assert.True(written.Ok);
        Assert.Equal(Folder, written.Value!.Config.LocalFolderPath);

        // And the list the window redraws afterwards says the same thing,
        // because it came back from ADL rather than being kept here.
        var stations = await serving.Link.StationsAsync();

        Assert.True(stations.Ok, $"{stations.Error}: {stations.Detail}");
        Assert.Equal(Folder, stations.Value!.Stations.Single().Config.LocalFolderPath);
        Assert.Equal(written.Value.ConfigVersion, stations.Value.ConfigVersion);
    }

    [Fact]
    public async Task A_refusal_arrives_as_the_code_the_window_switches_on()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServeAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var refused = await serving.Link.ConfigureAsync(11, new JsonObject
        {
            ["timezone"] = "Africa/Nairobi",
        });

        Assert.False(refused.Ok);
        Assert.True(refused.ServiceReached);
        Assert.Equal("read_only_fields", refused.Error);
    }

    [Fact]
    public async Task A_revoked_machine_reaches_the_window_as_an_instruction_to_pair_again()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServeAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        agent.Server.TokenRevoked = true;

        var refused = await serving.Link.ConfigureAsync(11, new JsonObject
        {
            ["file_pattern"] = "*.dat",
        });

        Assert.True(refused.NeedsRePairing);

        var after = await serving.Link.StatusAsync();

        Assert.True(after.Ok, $"{after.Error}: {after.Detail}");
        Assert.True(after.Value!.RePairNeeded);

        // The prompt the window shows is actionable: a fresh code puts the
        // machine back to work without anything else being touched.
        agent.Server.TokenRevoked = false;
        agent.Server.AddPairingCode("NEWC-0DE1");

        var paired = await serving.Link.PairAsync("NEWC-0DE1");

        Assert.True(paired.Ok);
        Assert.False(paired.Value!.RePairNeeded);
    }

    [Fact]
    public async Task A_pairing_code_that_ADL_will_not_take_is_reported_in_ADLs_own_words()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServeAsync(agent);

        var refused = await serving.Link.PairAsync("WRON-GC0D");

        Assert.False(refused.Ok);
        Assert.True(refused.ServiceReached);
        Assert.False(refused.NeedsRePairing);
        Assert.Contains("pairing code", refused.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- helpers ----------

    private async Task<ServedAgent> ServeAsync(AgentHarness agent)
    {
        var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var surface = new NamedPipeControlSurface(
            NullLogger<NamedPipeControlSurface>.Instance, _pipeName);

        var serving = surface.ServeAsync(agent.ControlService.HandleAsync, stopping.Token);

        var link = new AgentControlLink(
            () => new NamedPipeControlClient(TimeSpan.FromSeconds(10), _pipeName));

        // The surface binds its first pipe instance before it will accept
        // anything; asking before that is a race, not a failure.
        await WaitUntilListeningAsync(link);

        return new ServedAgent(link, stopping, serving);
    }

    private static async Task WaitUntilListeningAsync(AgentControlLink link)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if ((await link.StatusAsync()).ServiceReached)
            {
                return;
            }

            await Task.Delay(10);
        }
    }

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);

    private sealed class ServedAgent : IDisposable
    {
        private readonly CancellationTokenSource _stopping;
        private readonly Task _serving;

        public ServedAgent(AgentControlLink link, CancellationTokenSource stopping, Task serving)
        {
            Link = link;
            _stopping = stopping;
            _serving = serving;
        }

        public AgentControlLink Link { get; }

        public void Dispose()
        {
            _stopping.Cancel();

            try
            {
                _serving.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            _stopping.Dispose();
        }
    }
}
