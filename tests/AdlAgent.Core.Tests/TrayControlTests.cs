using System.Text.Json.Nodes;
using AdlAgent.Core.Api;
using AdlAgent.Core.Control;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Stories 2, 6, 7, 9 and 27: what a technician standing at the machine can
/// do without an ADL login.
/// </summary>
/// <remarks>
/// Driven at the control surface, which is the whole of what the tray is: a
/// WPF window that sends these commands and draws these answers. The spec
/// leaves the window itself unautomated on purpose -- "the tray stays thin"
/// is only true if everything it could get wrong lives here, so this is
/// where it is tested.
/// </remarks>
public class TrayControlTests
{
    private const string Folder = "C:\\VendorData\\Garissa";

    // ---------- the station list (story 6) ----------

    [Fact]
    public async Task The_tray_is_shown_every_station_ADL_linked_to_this_machine()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, Folder, "GARISSA_*.dat"),
            SyncConfigs.Link(12, "C:\\VendorData\\Mombasa", "MOMBASA_*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var stations = await Stations(agent);

        Assert.Equal([11L, 12L], stations.Select(station => Long(station, "station_link_id")));
        Assert.Equal(["Station 11", "Station 12"], stations.Select(station => Text(station, "station_name")));
        Assert.Equal("Vaisala AWS", Text(stations[0], "connection_name"));

        // The binding a technician owns, as it currently stands, so the tray
        // draws the folder box already filled in rather than empty.
        Assert.Equal(Folder, Text(stations[0]["config"]!.AsObject(), "local_folder_path"));
        Assert.Equal("GARISSA_*.dat", Text(stations[0]["config"]!.AsObject(), "file_pattern"));
    }

    [Fact]
    public async Task A_station_carries_what_the_last_cycle_did_for_it()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        agent.Files
            .Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n")
            .Add(Folder, "GARISSA_20260820.dat", Settled(agent), "09:00,20.9\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var station = (await Stations(agent)).Single();

        Assert.Equal(2, Int(station, "scanned"));
        Assert.Equal(2, Int(station, "offered"));
        Assert.Equal(2, Int(station, "uploaded"));
        Assert.Equal(0, Int(station, "failed"));
    }

    [Fact]
    public async Task A_station_the_cycle_could_not_scan_carries_the_reason_it_could_not()
    {
        await using var agent = new AgentHarness();

        // Linked in ADL, never bound to a folder here: the half-finished
        // setup the tray exists to let a technician finish.
        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, folder: ""));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var station = (await Stations(agent)).Single();

        Assert.Contains("No local folder", Text(station, "error"));
    }

    [Fact]
    public async Task A_machine_that_has_never_synced_is_told_so_rather_than_shown_an_empty_fleet()
    {
        await using var agent = new AgentHarness();

        var response = await agent.ControlService.HandleAsync(new ControlRequest(ControlProtocol.StationsCommand));

        Assert.True(response.Ok);
        Assert.Empty(response.Data!["stations"]!.AsArray());
        Assert.Null(response.Data["last_synced_at"]);
    }

    // ---------- live pattern validation (story 7) ----------

    [Fact]
    public async Task A_pattern_is_counted_against_the_folder_before_anyone_saves_it()
    {
        await using var agent = new AgentHarness();

        agent.Files
            .Add(Folder, "GARISSA_20260821.dat", Settled(agent))
            .Add(Folder, "GARISSA_20260820.dat", Settled(agent))
            .Add(Folder, "MOMBASA_20260821.dat", Settled(agent));

        var preview = await Preview(agent, new JsonObject
        {
            ["local_folder_path"] = Folder,
            ["file_pattern"] = "GARISSA_*.dat",
        });

        Assert.Equal(2, Int(preview, "matches"));
        Assert.Equal(3, Int(preview, "examined"));
        Assert.False(preview["truncated"]!.GetValue<bool>());

        // Newest first, and only a handful: the tray shows the technician
        // which files it means, not the folder.
        Assert.Equal(
            ["GARISSA_20260821.dat", "GARISSA_20260820.dat"],
            preview["sample"]!.AsArray().Select(name => name!.GetValue<string>()));
    }

    [Fact]
    public async Task A_pattern_that_matches_nothing_says_how_many_files_it_looked_at()
    {
        await using var agent = new AgentHarness();

        agent.Files
            .Add(Folder, "GARISSA_20260821.dat", Settled(agent))
            .Add(Folder, "GARISSA_20260820.dat", Settled(agent));

        var preview = await Preview(agent, new JsonObject
        {
            ["local_folder_path"] = Folder,
            ["file_pattern"] = "*.csv",
        });

        // The two numbers together are the whole diagnosis: files are there,
        // the pattern is what is wrong.
        Assert.Equal(0, Int(preview, "matches"));
        Assert.Equal(2, Int(preview, "examined"));
    }

    [Fact]
    public async Task A_folder_the_machine_cannot_read_is_told_apart_from_a_pattern_that_misses()
    {
        await using var agent = new AgentHarness();

        var preview = await Preview(agent, new JsonObject
        {
            ["local_folder_path"] = "C:\\Typo",
            ["file_pattern"] = "*.dat",
        });

        Assert.Equal(0, Int(preview, "matches"));
        Assert.Equal(0, Int(preview, "examined"));
        Assert.Contains("nothing", Text(preview, "problem"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_station_with_no_pattern_yet_is_told_to_type_one_rather_than_shown_a_zero()
    {
        await using var agent = new AgentHarness();

        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent));

        var preview = await Preview(agent, new JsonObject { ["local_folder_path"] = Folder });

        Assert.Equal(0, Int(preview, "matches"));
        Assert.Contains("pattern", Text(preview, "problem"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_preview_starts_from_the_station_it_names_and_changes_only_what_was_typed()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        agent.Files
            .Add(Folder, "GARISSA_20260821.dat", Settled(agent))
            .Add(Folder, "MOMBASA_20260821.dat", Settled(agent));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        // Nothing typed: the station's stored binding, counted as it stands.
        var stored = await Preview(agent, new JsonObject { ["station_link_id"] = 11 });

        Assert.Equal(1, Int(stored, "matches"));

        // The technician widens the pattern in the box. The folder comes from
        // the station; only the pattern is theirs.
        var typed = await Preview(agent, new JsonObject
        {
            ["station_link_id"] = 11,
            ["file_pattern"] = "*.dat",
        });

        Assert.Equal(2, Int(typed, "matches"));
        Assert.Equal(Folder, Text(typed, "local_folder_path"));
    }

    [Fact]
    public async Task A_direct_fetch_station_is_previewed_by_the_names_it_expects()
    {
        await using var agent = new AgentHarness();

        // 09:00 UTC is noon in Nairobi; the two names below are the ones the
        // grid lands on just behind it.
        agent.Files
            .Add(Folder, "GARISSA_202608211150.dat", Settled(agent))
            .Add(Folder, "GARISSA_202608211140.dat", Settled(agent))
            .Add(Folder, "MOMBASA_202608211150.dat", Settled(agent));

        var preview = await Preview(agent, new JsonObject
        {
            ["local_folder_path"] = Folder,
            ["listing_strategy"] = ListingStrategies.DirectFetch,
            ["direct_fetch_prefix"] = "GARISSA_",
            ["direct_fetch_interval_minutes"] = 10,
            ["direct_fetch_datetime_format"] = "yyyyMMddHHmm",
            ["direct_fetch_datetime_timezone"] = "Africa/Nairobi",
            ["direct_fetch_file_extension"] = ".dat",
        });

        Assert.Equal(2, Int(preview, "matches"));

        // The promise of the strategy, kept even here: a preview of a folder
        // nobody can afford to list must not list it.
        Assert.Equal(0, agent.Files.EnumerationsOf(Folder));
    }

    [Fact]
    public async Task A_direct_fetch_station_that_is_half_configured_says_what_is_missing()
    {
        await using var agent = new AgentHarness();

        var preview = await Preview(agent, new JsonObject
        {
            ["local_folder_path"] = Folder,
            ["listing_strategy"] = ListingStrategies.DirectFetch,
            ["direct_fetch_prefix"] = "GARISSA_",
        });

        Assert.Contains("interval", Text(preview, "problem"), StringComparison.OrdinalIgnoreCase);
    }

    // ---------- writing the app tier through to ADL (stories 8, 9) ----------

    [Fact]
    public async Task A_folder_bound_in_the_tray_is_written_to_ADL_and_the_version_moves()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, folder: ""));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var before = agent.Server.Config.ConfigVersion;

        var response = await Configure(agent, 11, new JsonObject
        {
            ["local_folder_path"] = Folder,
            ["file_pattern"] = "GARISSA_*.dat",
        });

        Assert.True(response.Ok);

        // It went to ADL, not into a file on the machine: the admin can see
        // this without calling anyone in-country (story 8).
        var written = Assert.Single(agent.Server.RequestsFor("station-links/11/config/"));

        Assert.Equal("PATCH", written.Method);
        Assert.Contains("GARISSA_*.dat", written.Body);

        Assert.True(agent.Server.Config.ConfigVersion > before);
        Assert.Equal(
            agent.Server.Config.ConfigVersion,
            response.Data!["config_version"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_binding_written_from_the_tray_is_what_the_very_next_cycle_scans()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, folder: ""));
        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        await Configure(agent, 11, new JsonObject
        {
            ["local_folder_path"] = Folder,
            ["file_pattern"] = "GARISSA_*.dat",
        });

        // No wait for the next sync: the write re-read the configuration, so
        // the technician who binds a folder sees files move rather than
        // wondering whether it took.
        await agent.Cycle.RunAsync();

        Assert.Equal("09:00,21.4\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
    }

    [Fact]
    public async Task The_tier_ADL_keeps_to_itself_is_refused_in_ADLs_own_words()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var response = await Configure(agent, 11, new JsonObject
        {
            ["local_folder_path"] = Folder,
            // What the data means stays with HQ (story 10).
            ["start_date"] = "2020-01-01T00:00:00Z",
        });

        Assert.False(response.Ok);
        Assert.Equal("read_only_fields", response.Error);
        Assert.Contains("start_date", response.Detail);
    }

    [Fact]
    public async Task Settings_ADL_will_not_store_come_back_as_the_sentence_ADL_wrote()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        // Cleared the folder box to retype it, and pressed Save. ADL
        // validates the tier before storing it, and refuses.
        var response = await Configure(agent, 11, new JsonObject
        {
            ["local_folder_path"] = "",
        });

        Assert.False(response.Ok);
        Assert.Equal("invalid_config", response.Error);
        Assert.Contains("folder", response.Detail);

        // Refused whole: the station is still bound to the folder it was.
        Assert.Equal(
            Folder,
            agent.Configuration.Current!.StationLinks.Single().Config.LocalFolderPath);
    }

    [Fact]
    public async Task A_station_ADL_has_since_moved_elsewhere_is_refused_by_ADL_and_survived()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        // This machine's cached configuration still has the station; ADL has
        // moved it to another device since. The local check cannot catch this
        // one, so it is ADL's 404 that has to arrive as something a person
        // can read.
        agent.Server.StationLinksUnknownToAdl.Add(11);

        var response = await Configure(agent, 11, new JsonObject
        {
            ["file_pattern"] = "*.dat",
        });

        Assert.False(response.Ok);
        Assert.Equal("not_found", response.Error);
        Assert.Contains("station link", response.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_write_with_no_ADL_to_write_to_is_not_quietly_applied_here_instead()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        agent.Server.Unreachable = true;

        var response = await Configure(agent, 11, new JsonObject
        {
            ["local_folder_path"] = "C:\\Somewhere\\Else",
        });

        Assert.False(response.Ok);
        Assert.Equal("adl_unreachable", response.Error);

        // ADL is the single source of truth for durable config: a write that
        // did not reach it did not happen, here or anywhere.
        Assert.Equal(
            Folder,
            agent.Configuration.Current!.StationLinks.Single().Config.LocalFolderPath);
    }

    [Fact]
    public async Task A_revoked_machine_is_told_to_re_pair_rather_than_that_the_write_failed()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        agent.Server.TokenRevoked = true;

        var response = await Configure(agent, 11, new JsonObject
        {
            ["local_folder_path"] = "C:\\Somewhere\\Else",
        });

        Assert.False(response.Ok);
        Assert.Equal("re_pair_needed", response.Error);

        var status = await agent.ControlService.HandleAsync(new ControlRequest(ControlProtocol.StatusCommand));

        Assert.True(status.Data!["re_pair_needed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Re_pairing_after_a_revocation_puts_the_machine_back_to_work()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));
        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        // The administrator revokes the device and issues a fresh code.
        agent.Server.TokenRevoked = true;

        await agent.Cycle.RunAsync();

        Assert.True((await agent.ControlService.HandleAsync(new ControlRequest(ControlProtocol.StatusCommand)))
            .Data!["re_pair_needed"]!.GetValue<bool>());

        agent.Server.TokenRevoked = false;
        agent.Server.IssuedToken = "device-token-after-rotation";

        var paired = await agent.PairAsync("NEWC-0DE1");

        Assert.True(paired.Ok);
        Assert.False(paired.Data!["re_pair_needed"]!.GetValue<bool>());

        await agent.Cycle.RunAsync();

        Assert.Equal("09:00,21.4\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
    }

    [Fact]
    public async Task A_station_this_device_does_not_have_is_never_written_to_ADL()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var response = await Configure(agent, 99, new JsonObject
        {
            ["local_folder_path"] = Folder,
        });

        Assert.False(response.Ok);
        Assert.Equal("unknown_station_link", response.Error);
        Assert.Empty(agent.Server.RequestsFor("station-links/99/config/"));
    }

    [Fact]
    public async Task An_unpaired_machine_has_nothing_to_write_configuration_to()
    {
        await using var agent = new AgentHarness();

        var response = await Configure(agent, 11, new JsonObject
        {
            ["local_folder_path"] = Folder,
        });

        Assert.False(response.Ok);
        Assert.Equal("not_paired", response.Error);
        Assert.Empty(agent.Server.Requests);
    }

    [Fact]
    public async Task A_write_that_names_no_station_is_refused_before_ADL_is_troubled()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();

        var response = await agent.ControlService.HandleAsync(new ControlRequest(
            ControlProtocol.ConfigureCommand,
            new JsonObject { ["config"] = new JsonObject { ["file_pattern"] = "*.dat" } }));

        Assert.False(response.Ok);
        Assert.Equal("invalid_request", response.Error);
    }

    // ---------- helpers ----------

    private static async Task<IReadOnlyList<JsonObject>> Stations(AgentHarness agent)
    {
        var response = await agent.ControlService.HandleAsync(new ControlRequest(ControlProtocol.StationsCommand));

        Assert.True(response.Ok);

        return response.Data!["stations"]!.AsArray()
            .Select(station => station!.AsObject())
            .ToList();
    }

    private static async Task<JsonObject> Preview(AgentHarness agent, JsonObject payload)
    {
        var response = await agent.ControlService.HandleAsync(
            new ControlRequest(ControlProtocol.PreviewCommand, payload));

        Assert.True(response.Ok);

        return response.Data!;
    }

    private static Task<ControlResponse> Configure(AgentHarness agent, long stationLinkId, JsonObject config) =>
        agent.ControlService.HandleAsync(new ControlRequest(
            ControlProtocol.ConfigureCommand,
            new JsonObject { ["station_link_id"] = stationLinkId, ["config"] = config }));

    private static string Text(JsonObject node, string key) => node[key]?.GetValue<string>() ?? "";

    private static int Int(JsonObject node, string key) => node[key]?.GetValue<int>() ?? -1;

    private static long Long(JsonObject node, string key) => node[key]!.GetValue<long>();

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
}
