using System.Text.Json.Nodes;
using AdlAgent.Core.Control;
using AdlAgent.Windows;
using AdlAgent.Windows.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The local conversation over its real transport, and the verbs that carry
/// it before there is a tray.
/// </summary>
/// <remarks>
/// Each test serves on a pipe name of its own, so the suite neither collides
/// with itself nor cares whether the machine running it happens to have a
/// real agent installed.
/// </remarks>
public class ControlTransportTests
{
    // Short on purpose: a pipe name becomes a unix socket path on macOS and
    // Linux, and that path has 104 characters to play with including the
    // temp directory it lands in.
    private readonly string _pipeName = $"adl-t{Guid.NewGuid():N}"[..13];

    [Fact]
    public async Task The_pipe_carries_a_command_to_the_agent_and_the_answer_back()
    {
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var surface = new NamedPipeControlSurface(
            NullLogger<NamedPipeControlSurface>.Instance, _pipeName);

        var serving = surface.ServeAsync(
            (request, _) => Task.FromResult(ControlResponse.Success(
                new JsonObject { ["echoed"] = request.Command })),
            stopping.Token);

        var response = await new NamedPipeControlClient(TimeSpan.FromSeconds(10), _pipeName)
            .AskAsync(new ControlRequest(ControlProtocol.StatusCommand), stopping.Token);

        Assert.True(response.Ok);
        Assert.Equal(ControlProtocol.StatusCommand, response.Data!["echoed"]!.GetValue<string>());

        await stopping.CancelAsync();
        await serving;
    }

    [Fact]
    public async Task A_technician_pairs_the_machine_from_the_command_line()
    {
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var surface = new NamedPipeControlSurface(
            NullLogger<NamedPipeControlSurface>.Instance, _pipeName);
        ControlRequest? received = null;

        var serving = surface.ServeAsync(
            (request, _) =>
            {
                received = request;

                return Task.FromResult(ControlResponse.Success(new JsonObject
                {
                    ["pairing_state"] = "Paired",
                    ["device_name"] = "Nairobi vendor server",
                    ["device_id"] = 7,
                    ["station_link_count"] = 3,
                }));
            },
            stopping.Token);

        var output = new StringWriter();
        var exitCode = await AgentCli.RunAsync(
            ["pair", "KX7M-93QA"], output, new NamedPipeControlClient(TimeSpan.FromSeconds(10), _pipeName));

        Assert.Equal(0, exitCode);
        Assert.Equal(ControlProtocol.PairCommand, received!.Command);
        Assert.Equal("KX7M-93QA", received.Payload!["pairing_code"]!.GetValue<string>());
        Assert.Contains("Nairobi vendor server", output.ToString());

        await stopping.CancelAsync();
        await serving;
    }

    [Fact]
    public async Task A_machine_where_nothing_is_listening_says_so_plainly()
    {
        var output = new StringWriter();

        // Nothing is serving this pipe: the service is not running, which is
        // the commonest thing to be wrong and deserves a sentence rather than
        // a stack trace.
        var exitCode = await AgentCli.RunAsync(
            ["status"], output, new NamedPipeControlClient(TimeSpan.FromSeconds(1), _pipeName));

        Assert.Equal(1, exitCode);
        Assert.Contains("not answering", output.ToString());
    }

    [Fact]
    public async Task Pairing_without_a_code_explains_itself()
    {
        var output = new StringWriter();

        Assert.Equal(2, await AgentCli.RunAsync(["pair"], output));
        Assert.Contains("Usage: adl-agent pair", output.ToString());
    }

    [Fact]
    public void The_verbs_are_told_apart_from_host_configuration()
    {
        Assert.True(AgentCli.Handles(["pair", "KX7M-93QA"]));
        Assert.True(AgentCli.Handles(["status"]));
        Assert.False(AgentCli.Handles([]));
        Assert.False(AgentCli.Handles(["--Agent:AdlBaseUrl=https://adl.example.org"]));
    }

    [Fact]
    public void A_refusal_reaches_the_technician_as_the_sentence_ADL_wrote()
    {
        var described = AgentCli.Describe(ControlResponse.Failure(
            "invalid_pairing_code", "That pairing code is not recognised."));

        Assert.Equal("That pairing code is not recognised.", described);
    }

    [Fact]
    public async Task A_local_client_that_never_finishes_its_sentence_cannot_wedge_the_agent()
    {
        using var wire = new MemoryStream(
            new byte[ControlProtocol.MaxMessageBytes + 1]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ControlProtocol.ReadRequestAsync(wire));
    }
}
