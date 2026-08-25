using AdlAgent.TestSupport;
using AdlAgent.Core.Update;

namespace AdlAgent.Core.Tests;

/// <summary>
/// A machine that was never told where its ADL is.
/// </summary>
/// <remarks>
/// The state an MSI run without <c>ADLURL</c> leaves behind, and the one
/// state where the machine is doing nothing at all. It used to be the
/// quietest thing this agent could do: <c>[Required]</c> on the address, with
/// <c>ValidateOnStart</c> above it, threw before the host ran -- so a service
/// Windows Installer configures to restart on failure crash-looped on a
/// machine nobody can reach, and the only trace was an Event Log entry
/// nobody was watching.
/// <para>
/// So the agent stays up, says what is wrong and says who can fix it, and
/// makes no calls it has nowhere to send. See wmo-raf/adl#294.
/// </para>
/// </remarks>
public class NotConfiguredTests
{
    /// <summary>An agent that was given no address at all.</summary>
    private static AgentHarness Unconfigured(string address = "") =>
        new(settings: new Dictionary<string, string?> { ["Agent:AdlBaseUrl"] = address });

    [Fact]
    public async Task The_agent_starts_and_stays_up_with_no_address()
    {
        await using var agent = Unconfigured();

        // The whole point. Before this, starting was the thing it could not
        // do -- and a service that cannot start is a service Windows keeps
        // restarting.
        await agent.StartAsync();

        Assert.False(agent.Status.Read().Configured);
    }

    [Fact]
    public async Task It_calls_nothing_it_has_nowhere_to_send()
    {
        await using var agent = Unconfigured();

        await agent.StartAsync();

        // Deliberately not AtRestAsync: the loops do not go to sleep on the
        // wake signal, they never begin. Waiting for three loops to settle
        // would wait for ever, and that difference is the behaviour.
        agent.Time.Advance(TimeSpan.FromMinutes(30));

        Assert.Empty(agent.Server.Requests);
    }

    [Fact]
    public async Task The_status_says_which_setting_is_missing()
    {
        await using var agent = Unconfigured();

        var status = agent.Status.Read();

        Assert.False(status.Configured);

        // Named, because the person reading it has to go and set it.
        Assert.Contains("Agent:AdlBaseUrl", status.ConfigurationProblem);
    }

    [Fact]
    public async Task An_address_that_is_not_https_is_refused_the_same_way_a_missing_one_is()
    {
        await using var agent = Unconfigured("http://adl.example.org");

        var status = agent.Status.Read();

        // Configured-but-refused and not-configured-at-all are the same
        // outcome for this machine -- it has nowhere it is willing to send --
        // and the sentence is what tells them apart for whoever is fixing it.
        Assert.False(status.Configured);
        Assert.Contains("https", status.ConfigurationProblem);
    }

    [Fact]
    public async Task A_configured_machine_says_so_and_carries_no_advice()
    {
        await using var agent = new AgentHarness();

        var status = agent.Status.Read();

        Assert.True(status.Configured);
        Assert.Null(status.ConfigurationProblem);
        Assert.Null(status.ConfigurationHint);
    }

    // ---------- who can fix it, which is not the same on both tiers ----------

    [Fact]
    public async Task The_service_tier_is_told_the_file_an_administrator_edits()
    {
        await using var agent = Unconfigured();

        agent.Updates.Tier = UpdateTiers.Service;
        agent.HostLifecycle.SettingsFilePath = @"C:\ProgramData\ADL Agent\agent.ini";

        var hint = agent.Status.Read().ConfigurationHint;

        // The verb, because there is one: an administrator told to edit the
        // file and restart the service has three steps to get right on a
        // machine nobody can reach, and adl-agent set-url is all three.
        Assert.Contains("adl-agent set-url", hint);

        // And still the path, because it is what a technician reads out over
        // a telephone to the administrator who has to act on it.
        Assert.Contains(@"C:\ProgramData\ADL Agent\agent.ini", hint);
        Assert.Contains("administrator", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_per_user_tier_is_told_something_it_can_do_without_an_administrator()
    {
        await using var agent = Unconfigured();

        agent.Updates.Tier = UpdateTiers.User;

        var hint = agent.Status.Read().ConfigurationHint;

        // This tier exists for a technician who has no administrator rights,
        // so an answer that needs them would be no answer. It is still a
        // command line on the one tier whose whole reason for existing is
        // somebody who should not need one -- a known gap, recorded in the
        // README rather than discovered on a country server.
        Assert.Contains("setx Agent__AdlBaseUrl", hint);
        Assert.DoesNotContain("administrator", hint, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- and out of it again ----------

    [Fact]
    public async Task Giving_the_machine_an_address_brings_it_to_life_when_it_restarts()
    {
        // One machine, across a restart: the same state store is what makes
        // the second harness the same server rather than a different one.
        var store = new InMemoryAgentStateStore();

        await using (var unconfigured = new AgentHarness(
            store, new Dictionary<string, string?> { ["Agent:AdlBaseUrl"] = "" }))
        {
            await unconfigured.StartAsync();

            unconfigured.Time.Advance(TimeSpan.FromMinutes(30));

            Assert.Empty(unconfigured.Server.Requests);
        }

        // What actually happens on the machine: something writes the address
        // and the agent is restarted. Nothing re-reads it in place -- the
        // settings file is read once at start-up and the environment is taken
        // at logon -- which is why the loops return rather than wait.
        await using var configured = new AgentHarness(store);

        await configured.StartAsync();
        await configured.PairAsync();

        Assert.True(configured.Status.Read().Configured);

        // Beating is the observable that means the loops are running again.
        // A machine that had only been repointed and not restarted would sit
        // exactly as silent as the block above.
        Assert.True(
            await configured.Server.WaitForRequestsAsync("heartbeat/", 1),
            "The heartbeat loop did not run after the machine was given an address.");
    }
}
