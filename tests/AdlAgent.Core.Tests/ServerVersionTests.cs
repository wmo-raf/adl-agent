using System.Text.Json;
using System.Text.Json.Nodes;
using AdlAgent.Core.Api;
using AdlAgent.Core.Control;
using AdlAgent.Core.Serialization;
using AdlAgent.TestSupport;
using AdlAgent.Tray;
using AdlAgent.Windows;

namespace AdlAgent.Core.Tests;

/// <summary>
/// What the instance at the other end is running, and how a machine says so.
/// </summary>
/// <remarks>
/// Nothing here changes what the agent does: these two strings are read by a
/// person and acted on by nobody. So what is pinned is where they come from
/// and what is said when they are missing -- because the failure this exists
/// to stop is not a crash, it is a technician being shown a version that was
/// true at some other time, or a blank where a fact should be.
/// <para>
/// The reason they ride the sync response rather than the heartbeat is a
/// testable claim and is tested:
/// <see cref="An_unreachable_ADL_still_says_what_it_was_running"/>.
/// </para>
/// </remarks>
public class ServerVersionTests
{
    /// <summary>A sync response from an ADL too old to describe itself.</summary>
    private static SyncResponse Silent() =>
        FakeAdlServer.SampleConfig() with { Server = new ServerInfo() };

    // ---------- what reaches the machine ----------

    [Fact]
    public async Task The_versions_ADL_serves_reach_the_machines_status()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var status = agent.Status.Read();

        Assert.True(status.AdlReportedItsVersion);
        Assert.Equal("0.8.14", status.AdlVersion);
        Assert.Equal("0.4.0", status.PluginVersion);
    }

    [Fact]
    public async Task An_unreachable_ADL_still_says_what_it_was_running()
    {
        // The whole argument for carrying this on the sync response. A sync
        // is cached to disk byte for byte, so the answer survives the link
        // going down -- which is when somebody is most likely to be reading
        // it. On the heartbeat it would be blank here, and blank again after
        // every service restart.
        await using var agent = new AgentHarness();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        agent.Server.Unreachable = true;

        var configuration = await agent.Configuration.RefreshAsync();

        Assert.NotNull(configuration);
        Assert.True(configuration.FromCache);

        var status = agent.Status.Read();

        Assert.True(status.AdlReportedItsVersion);
        Assert.Equal("0.8.14", status.AdlVersion);
        Assert.Equal("0.4.0", status.PluginVersion);
    }

    [Fact]
    public async Task An_ADL_that_never_says_leaves_the_machine_saying_so()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = Silent();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var status = agent.Status.Read();

        Assert.False(status.AdlReportedItsVersion);
        Assert.Equal("", status.AdlVersion);
        Assert.Equal("", status.PluginVersion);
    }

    // ---------- what the window draws ----------

    [Fact]
    public async Task The_window_puts_both_numbers_on_one_line()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal("0.8.14  ·  agent plugin 0.4.0", window.AdlVersion);
    }

    [Fact]
    public async Task An_ADL_too_old_to_say_is_named_rather_than_dashed()
    {
        // Most of the fleet will be on one of these for a while, so this
        // wording is what most machines show. It is worth more than a dash:
        // "too old to say" puts a lower bound on the far end's version, which
        // is a fact about the far end. A dash reads as this machine having
        // failed to fetch something.
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = Silent();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal("Not reported — this ADL predates the field", window.AdlVersion);
    }

    [Fact]
    public async Task Half_an_answer_is_still_shown_as_an_answer()
    {
        // An instance that sent the block with one string empty has told us
        // something is wrong at its end and not at this one, so the row says
        // what it knows rather than falling back to "predates the field" --
        // which would be this machine inventing a reason.
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = FakeAdlServer.SampleConfig() with
        {
            Server = new ServerInfo { AdlVersion = "0.8.14", PluginVersion = "" },
        };

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal("0.8.14  ·  agent plugin unknown", window.AdlVersion);
    }

    [Fact]
    public async Task A_machine_that_has_never_synced_shows_a_dash_like_every_other_row()
    {
        // Told apart from the sentence above on purpose. Nothing has been
        // asked of ADL yet, so nothing about ADL's age has been learnt, and
        // claiming otherwise would be inventing the one fact this row exists
        // to report. The grid it sits in is hidden on this machine anyway.
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal("-", window.AdlVersion);
    }

    // ---------- what the command line says ----------

    [Fact]
    public async Task The_command_line_names_the_far_ends_versions_apart_from_this_ones()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var described = AgentCli.Describe(
            ControlResponse.Success(Serialised(agent.Status.Read())));

        Assert.Contains("ADL ver:  0.8.14 (agent plugin 0.4.0)", described);

        // And the machine's own number is still its own line. Two lines both
        // labelled "Version" on a screen whose whole job is telling three
        // version numbers apart would be worse than saying nothing.
        Assert.Contains($"Version:  {Core.AgentVersion.Current}", described);
    }

    [Fact]
    public async Task Nothing_this_program_prints_needs_a_code_page()
    {
        // The tray's row separates the two numbers with a middle dot; this
        // one must not. Nothing sets Console.OutputEncoding, so `adl-agent
        // status` prints through whatever code page the console has, and on
        // cp850 or cp437 a non-ASCII byte is mojibake in the one output that
        // gets read out over a telephone.
        //
        // Over the whole of Describe rather than the line just added: every
        // line this program prints was ASCII before, and the cheapest way to
        // keep that true is to say so once here.
        await using var agent = new AgentHarness();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var described = AgentCli.Describe(
            ControlResponse.Success(Serialised(agent.Status.Read())));

        Assert.DoesNotContain(described, character => character > '\x7F');
    }

    [Fact]
    public async Task The_command_line_says_nothing_when_the_far_end_said_nothing()
    {
        // The rule the Updates and Last error lines already follow. A window
        // has room to explain a silence; six lines read down a telephone do
        // not.
        await using var agent = new AgentHarness();

        agent.Server.Config = Silent();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var described = AgentCli.Describe(
            ControlResponse.Success(Serialised(agent.Status.Read())));

        Assert.DoesNotContain("ADL ver:", described);
    }

    // ---------- the other direction of the version skew ----------

    [Fact]
    public void A_response_from_an_ADL_newer_than_this_agent_is_read_without_choking()
    {
        // The old-agent/new-ADL half of the skew, which is the half nobody
        // controls: instances are upgraded by a person per country, and an
        // agent in the field is whatever it is. Pinned here rather than
        // trusted to a serializer default, because the cost of that default
        // ever changing is a fleet that stops reading its configuration.
        const string body = """
            {
              "config_version": 9,
              "server": {
                "adl_version": "0.9.0",
                "plugin_version": "0.5.0",
                "built_at": "2026-09-01T00:00:00Z"
              },
              "something_this_agent_has_never_heard_of": {"nested": [1, 2, 3]},
              "device": {"id": 7, "name": "Ouagadougou-01"}
            }
            """;

        var sync = JsonSerializer.Deserialize<SyncResponse>(body, AgentJson.Options);

        Assert.NotNull(sync);
        Assert.Equal(9, sync.ConfigVersion);
        Assert.Equal("0.9.0", sync.Server.AdlVersion);
        Assert.Equal("0.5.0", sync.Server.PluginVersion);
        Assert.True(sync.Server.Reported);
    }

    [Fact]
    public void A_response_with_no_server_block_at_all_leaves_it_unreported()
    {
        var sync = JsonSerializer.Deserialize<SyncResponse>(
            """{"config_version": 9}""", AgentJson.Options);

        Assert.NotNull(sync);
        Assert.False(sync.Server.Reported);
    }

    private static JsonObject Serialised(object value) =>
        JsonSerializer.SerializeToNode(value, AgentJson.Options)!.AsObject();
}
