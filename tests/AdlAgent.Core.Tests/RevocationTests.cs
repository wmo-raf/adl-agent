using AdlAgent.Core.Control;
using AdlAgent.Core.Pairing;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Story 4, from the machine's end: an administrator revokes a device, and
/// the machine stops.
/// </summary>
/// <remarks>
/// A 401 is the only server answer that changes what the agent is rather than
/// what one call did, so these tests check all three consequences: it stops
/// talking, it says why, and it still says why after a restart.
/// </remarks>
public class RevocationTests
{
    [Fact]
    public async Task A_refused_token_stops_the_machine_talking_to_ADL()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();
        await agent.HeartbeatLoop.BeatAsync();

        agent.Server.TokenRevoked = true;

        await agent.HeartbeatLoop.BeatAsync();

        Assert.Equal(PairingState.RePairNeeded, agent.Session.State);
        Assert.Null(agent.Session.ActiveToken);

        var callsSoFar = agent.Server.Requests.Count;

        await agent.HeartbeatLoop.BeatAsync();
        await agent.Configuration.RefreshAsync();
        await agent.SyncLoop.SyncAsync();

        // Not one more request: the machine has been told to stop, and
        // hammering an instance with calls that can only be refused is how a
        // revoked device becomes an operational problem of its own.
        Assert.Equal(callsSoFar, agent.Server.Requests.Count);
    }

    [Fact]
    public async Task A_sync_that_is_refused_stops_the_machine_too()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();

        agent.Server.TokenRevoked = true;

        await agent.Configuration.RefreshAsync();

        Assert.Equal(PairingState.RePairNeeded, agent.Session.State);
    }

    [Fact]
    public async Task The_machine_says_re_pair_needed_rather_than_going_quiet()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        agent.Server.TokenRevoked = true;

        await agent.HeartbeatLoop.BeatAsync();

        var status = agent.Status.Read();

        Assert.True(status.RePairNeeded);
        Assert.Equal(nameof(PairingState.RePairNeeded), status.PairingState);

        // Still shows which device it was and what it was working from: a
        // technician needs to know this machine was set up and has been cut
        // off, not that nothing was ever configured here.
        Assert.Equal(agent.Server.Device.Name, status.DeviceName);
        Assert.Equal(1, status.StationLinkCount);
    }

    [Fact]
    public async Task A_restart_does_not_hide_a_revocation()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();

        agent.Server.TokenRevoked = true;

        await agent.HeartbeatLoop.BeatAsync();

        // Restarting the service is the first thing anyone tries. It must not
        // make the machine look healthy again until its next refused call.
        Assert.True(agent.Store.Load().RePairNeeded);
    }

    [Fact]
    public async Task Pairing_again_puts_the_machine_back_to_work()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();

        agent.Server.TokenRevoked = true;

        await agent.HeartbeatLoop.BeatAsync();

        Assert.Equal(PairingState.RePairNeeded, agent.Session.State);

        // The administrator rotates the token: a new code, a new token.
        agent.Server.TokenRevoked = false;
        agent.Server.IssuedToken = "rotated-token-9876543210";

        var response = await agent.PairAsync("NEW1-CODE");

        Assert.True(response.Ok);
        Assert.Equal(PairingState.Paired, agent.Session.State);

        await agent.HeartbeatLoop.BeatAsync();

        Assert.Equal("rotated-token-9876543210", agent.Server.RequestsFor("heartbeat/")[^1].BearerToken);
        Assert.False(agent.Store.Load().RePairNeeded);
    }

    [Fact]
    public async Task A_machine_that_was_never_paired_is_unpaired_not_cut_off()
    {
        await using var agent = new AgentHarness();

        var status = agent.Status.Read();

        Assert.False(status.RePairNeeded);
        Assert.Equal(nameof(PairingState.Unpaired), status.PairingState);
        Assert.Null(status.DeviceId);
    }
}
