using AdlAgent.Core.Api;
using AdlAgent.Core.Pairing;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// What happens when a technician types the code the administrator gave them.
/// </summary>
/// <remarks>
/// Driven through the control surface rather than by calling the session,
/// because that is the path the tray takes and the only path a real pairing
/// ever travels.
/// </remarks>
public class PairingTests
{
    [Fact]
    public async Task A_pairing_code_becomes_a_stored_device_token()
    {
        await using var agent = new AgentHarness();

        var response = await agent.PairAsync("KX7M-93QA");

        Assert.True(response.Ok);
        Assert.Equal(PairingState.Paired, agent.Session.State);
        Assert.Equal(agent.Server.IssuedToken, agent.Store.Load().Token);
        Assert.Equal(agent.Server.Device.Name, agent.Session.Device?.Name);
    }

    [Fact]
    public async Task The_code_is_sent_to_ADL_with_this_agents_version()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync("KX7M-93QA");

        var pair = Assert.Single(agent.Server.RequestsFor("pair/"));

        Assert.Equal("POST", pair.Method);
        Assert.Contains("KX7M-93QA", pair.Body);
        Assert.False(string.IsNullOrWhiteSpace(pair.Header(AdlApiClient.VersionHeader)));

        // Pairing is the one call that must work without a credential, so it
        // must not be sent with one.
        Assert.Null(pair.BearerToken);
    }

    [Fact]
    public async Task A_code_ADL_does_not_recognise_leaves_the_machine_unpaired()
    {
        await using var agent = new AgentHarness();

        var response = await agent.ControlService.HandleAsync(
            new Core.Control.ControlRequest(
                Core.Control.ControlProtocol.PairCommand,
                new System.Text.Json.Nodes.JsonObject { ["pairing_code"] = "NOPE-NOPE" }));

        Assert.False(response.Ok);
        Assert.Equal("invalid_pairing_code", response.Error);

        // The sentence ADL wrote reaches the technician unrewritten -- it is
        // the one that tells them whether to re-type it or ask for a new one.
        Assert.Contains("administrator", response.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PairingState.Unpaired, agent.Session.State);
        Assert.Null(agent.Store.Load().Token);
    }

    [Fact]
    public async Task A_pairing_code_is_good_once()
    {
        await using var agent = new AgentHarness();

        Assert.True((await agent.PairAsync("KX7M-93QA")).Ok);

        var second = await agent.ControlService.HandleAsync(
            new Core.Control.ControlRequest(
                Core.Control.ControlProtocol.PairCommand,
                new System.Text.Json.Nodes.JsonObject { ["pairing_code"] = "KX7M-93QA" }));

        Assert.False(second.Ok);

        // Still paired from the first redemption: a re-used code is a failed
        // attempt, not a reason to un-pair a working machine.
        Assert.Equal(PairingState.Paired, agent.Session.State);
    }

    [Fact]
    public async Task Every_call_after_pairing_carries_the_token()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();
        await agent.HeartbeatLoop.BeatAsync();

        var authenticated = agent.Server.Requests
            .Where(request => request.Path != "pair/")
            .ToList();

        Assert.NotEmpty(authenticated);
        Assert.All(authenticated, request =>
        {
            Assert.Equal(agent.Server.IssuedToken, request.BearerToken);
            Assert.Equal(AgentVersion.Current, request.Header(AdlApiClient.VersionHeader));
        });
    }

    [Fact]
    public async Task An_unpaired_machine_says_nothing_to_ADL()
    {
        await using var agent = new AgentHarness();

        await agent.HeartbeatLoop.BeatAsync();
        await agent.Configuration.RefreshAsync();

        Assert.Empty(agent.Server.Requests);
    }
}
