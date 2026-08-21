namespace AdlAgent.Core.Tests;

/// <summary>
/// The only settings that live on the machine, and the one thing they refuse.
/// </summary>
public class AgentOptionsTests
{
    [Fact]
    public void The_agent_calls_the_plugins_versioned_surface()
    {
        var options = new AgentOptions { AdlBaseUrl = "https://adl.example.org" };

        Assert.Equal(
            new Uri("https://adl.example.org/plugins/api/agent/v1/"),
            options.ResolveApiBaseAddress());
    }

    [Fact]
    public void A_trailing_slash_or_its_absence_makes_no_difference()
    {
        Assert.Equal(
            new AgentOptions { AdlBaseUrl = "https://adl.example.org" }.ResolveApiBaseAddress(),
            new AgentOptions { AdlBaseUrl = "https://adl.example.org/" }.ResolveApiBaseAddress());
    }

    [Fact]
    public void Plain_HTTP_to_another_machine_is_refused()
    {
        var options = new AgentOptions { AdlBaseUrl = "http://adl.example.org" };

        var refusal = Assert.Throws<InvalidOperationException>(options.ResolveApiBaseAddress);

        // The device token travels on every call. A fleet configured without
        // TLS is worth failing to start over.
        Assert.Contains("https", refusal.Message);
    }

    [Fact]
    public void Loopback_stays_reachable_over_HTTP_for_the_test_fixture()
    {
        var options = new AgentOptions { AdlBaseUrl = "http://127.0.0.1:8080" };

        Assert.Equal(
            new Uri("http://127.0.0.1:8080/plugins/api/agent/v1/"),
            options.ResolveApiBaseAddress());
    }

    [Fact]
    public void A_machine_that_was_never_pointed_at_an_instance_says_which_setting_is_missing()
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            new AgentOptions().ResolveApiBaseAddress);

        Assert.Contains("Agent:AdlBaseUrl", refusal.Message);
    }
}
