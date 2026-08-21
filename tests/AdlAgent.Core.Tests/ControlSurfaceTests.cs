using System.Text.Json.Nodes;
using AdlAgent.Core.Control;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The conversation the tray has with the service.
/// </summary>
/// <remarks>
/// Tested through the protocol and the seam, not through the Windows pipe:
/// what has to be right here is what the commands mean and how they are
/// framed, and both are the core's, identical on the Linux head that follows.
/// </remarks>
public class ControlSurfaceTests
{
    [Fact]
    public async Task Status_tells_the_tray_everything_it_draws()
    {
        await using var agent = new AgentHarness();

        await agent.StartAsync();
        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();
        await agent.HeartbeatLoop.BeatAsync();

        var response = await agent.Control.SendAsync(new ControlRequest(ControlProtocol.StatusCommand));

        Assert.True(response.Ok);

        var status = response.Data!;

        Assert.Equal("Paired", status["pairing_state"]!.GetValue<string>());
        Assert.Equal(agent.Server.Device.Name, status["device_name"]!.GetValue<string>());
        Assert.Equal(1, status["station_link_count"]!.GetValue<int>());
        Assert.Equal("online", status["fleet_status"]!.GetValue<string>());
        Assert.Equal(agent.Server.BaseAddress.ToString().TrimEnd('/'), status["adl_url"]!.GetValue<string>());
        Assert.False(status["config_from_cache"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_tray_learns_that_the_agent_is_working_from_its_cache()
    {
        await using var agent = new AgentHarness();

        await agent.StartAsync();
        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        agent.Server.Unreachable = true;

        await agent.Configuration.RefreshAsync();

        var response = await agent.Control.SendAsync(new ControlRequest(ControlProtocol.StatusCommand));

        Assert.True(response.Data!["config_from_cache"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_unknown_command_is_refused_by_name()
    {
        await using var agent = new AgentHarness();

        await agent.StartAsync();

        var response = await agent.Control.SendAsync(new ControlRequest("upload-everything-now"));

        Assert.False(response.Ok);
        Assert.Equal("unknown_command", response.Error);
    }

    [Fact]
    public async Task Pairing_without_a_code_is_refused_before_ADL_is_troubled()
    {
        await using var agent = new AgentHarness();

        await agent.StartAsync();

        var response = await agent.Control.SendAsync(new ControlRequest(ControlProtocol.PairCommand));

        Assert.False(response.Ok);
        Assert.Equal("invalid_pairing_code", response.Error);
        Assert.Empty(agent.Server.Requests);
    }

    [Fact]
    public async Task An_unreachable_ADL_is_reported_as_such_rather_than_as_a_bad_code()
    {
        await using var agent = new AgentHarness();

        await agent.StartAsync();

        agent.Server.AddPairingCode("KX7M-93QA");
        agent.Server.Unreachable = true;

        var response = await agent.Control.SendAsync(new ControlRequest(
            ControlProtocol.PairCommand,
            new JsonObject { ["pairing_code"] = "KX7M-93QA" }));

        Assert.False(response.Ok);
        Assert.Equal("adl_unreachable", response.Error);
    }

    [Fact]
    public async Task A_request_and_its_answer_survive_the_wire()
    {
        // The framing itself: one JSON object per line, in both directions.
        using var wire = new MemoryStream();

        await ControlProtocol.WriteRequestAsync(
            wire,
            new ControlRequest(ControlProtocol.PairCommand, new JsonObject { ["pairing_code"] = "KX7M-93QA" }));

        wire.Position = 0;

        var request = await ControlProtocol.ReadRequestAsync(wire);

        Assert.NotNull(request);
        Assert.Equal(ControlProtocol.PairCommand, request.Command);
        Assert.Equal("KX7M-93QA", request.Payload!["pairing_code"]!.GetValue<string>());

        using var back = new MemoryStream();

        await ControlProtocol.WriteResponseAsync(
            back, ControlResponse.Failure("invalid_pairing_code", "Ask for a new code."));

        back.Position = 0;

        var response = await ControlProtocol.ReadResponseAsync(back);

        Assert.NotNull(response);
        Assert.False(response.Ok);
        Assert.Equal("invalid_pairing_code", response.Error);
        Assert.Equal("Ask for a new code.", response.Detail);
    }

    [Fact]
    public async Task A_client_that_hangs_up_mid_sentence_is_not_a_message()
    {
        using var wire = new MemoryStream("{\"command\":\"stat"u8.ToArray());

        Assert.Null(await ControlProtocol.ReadRequestAsync(wire));
    }
}
