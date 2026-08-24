using System.Text.Json.Nodes;
using AdlAgent.Core.Control;
using AdlAgent.Windows.Platform;

namespace AdlAgent.Windows;

/// <summary>
/// The two verbs a technician standing at the machine needs before there is
/// a tray to click.
/// </summary>
/// <remarks>
/// <c>adl-agent pair &lt;code&gt;</c> and <c>adl-agent status</c> talk to the
/// running service over the same control protocol the tray will use. They
/// exist because an agent that cannot be paired cannot be installed: without
/// them the whole product waits on a WPF window.
/// </remarks>
public static class AgentCli
{
    /// <summary>True when these arguments are a verb rather than host configuration.</summary>
    public static bool Handles(string[] args) =>
        args.Length > 0 && args[0] is "pair" or "status";

    /// <summary>Run the verb. Returns the process exit code.</summary>
    /// <param name="client">
    /// The way to reach the running agent. Supplied by tests so they never
    /// depend on whether this machine happens to have an agent running.
    /// </param>
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, NamedPipeControlClient? client = null)
    {
        var request = args[0] switch
        {
            "pair" when args.Length > 1 => new ControlRequest(
                ControlProtocol.PairCommand,
                new JsonObject { ["pairing_code"] = args[1] }),
            "pair" => null,
            _ => new ControlRequest(ControlProtocol.StatusCommand),
        };

        if (request is null)
        {
            await output.WriteLineAsync(
                "Usage: adl-agent pair <pairing-code>    (get the code from your ADL administrator)")
                .ConfigureAwait(false);

            return 2;
        }

        ControlResponse response;

        try
        {
            client ??= new NamedPipeControlClient();

            response = await client.AskAsync(request).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException)
        {
            // The commonest thing that goes wrong, and the one worth a
            // sentence rather than a stack trace.
            await output.WriteLineAsync(
                "The ADL Agent service is not answering. Check that it is running.")
                .ConfigureAwait(false);

            return 1;
        }

        await output.WriteLineAsync(Describe(response)).ConfigureAwait(false);

        return response.Ok ? 0 : 1;
    }

    /// <summary>The answer, as a person would want it read out.</summary>
    public static string Describe(ControlResponse response)
    {
        if (!response.Ok)
        {
            return response.Detail ?? response.Error ?? "The agent refused that.";
        }

        var status = response.Data;

        if (status is null)
        {
            return "Done.";
        }

        // A machine with no address gets said first and said plainly. The
        // rest of the block below is about reaching ADL, and none of it means
        // anything on a machine that has not been told where ADL is -- an
        // empty "ADL:" row beside "State: Unpaired" reads as a machine
        // waiting for a pairing code, which is a different problem with a
        // different person to call.
        if (status["configured"]?.GetValue<bool>() == false)
        {
            var unconfigured = new List<string>
            {
                "ADL:      not configured",
                $"Problem:  {Text(status, "configuration_problem")}",
                $"Fix:      {Text(status, "configuration_hint")}",
                $"Version:  {Text(status, "agent_version")}",
                "",
                "The agent is running and will do nothing until it has an address.",
            };

            return string.Join(Environment.NewLine, unconfigured);
        }

        var lines = new List<string>
        {
            $"ADL:      {Text(status, "adl_url")}",
            $"State:    {Text(status, "pairing_state")}",
            $"Device:   {Text(status, "device_name")} (#{Text(status, "device_id")})",
            $"Stations: {Text(status, "station_link_count")}",
            $"Fleet:    {Text(status, "fleet_status")} at {Text(status, "last_heartbeat_at")}",
            $"Version:  {Text(status, "agent_version")}",
        };

        if (Text(status, "update_detail") is { Length: > 0 } update and not "-")
        {
            lines.Add($"Updates:  {update}");
        }

        if (status["config_from_cache"]?.GetValue<bool>() == true)
        {
            lines.Add("Note:     working from the cached configuration; ADL was not reachable.");
        }

        if (Text(status, "last_error") is { Length: > 0 } error and not "-")
        {
            lines.Add($"Last error: {error}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Text(JsonObject status, string key)
    {
        var value = status[key];

        return value is null ? "-" : value.ToString();
    }
}
