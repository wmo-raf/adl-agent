using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdlAgent.Core.Serialization;

namespace AdlAgent.Core.Control;

/// <summary>
/// What the local UI and the agent say to each other.
/// </summary>
/// <remarks>
/// Defined here, in the core, rather than beside either transport: the WPF
/// tray on Windows and the CLI on Linux are two views of one conversation,
/// and a protocol that lived in a head would be reimplemented -- and would
/// drift -- the first time a second head existed.
/// <para>
/// The framing is one JSON object per line, UTF-8. A line, because the two
/// ends are on the same machine and a length prefix would buy nothing but a
/// way to get the length wrong; and because a technician debugging a stuck
/// service can drive the whole surface by hand.
/// </para>
/// </remarks>
public static class ControlProtocol
{
    /// <summary>Ask what the agent is doing. No payload.</summary>
    public const string StatusCommand = "status";

    /// <summary>Redeem a pairing code. Payload: <c>{"pairing_code": "..."}</c>.</summary>
    public const string PairCommand = "pair";

    /// <summary>Read one request, or <c>null</c> when the client hung up.</summary>
    public static async Task<ControlRequest?> ReadRequestAsync(
        Stream stream, CancellationToken cancellationToken = default)
    {
        var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);

        if (line is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<ControlRequest>(line, AgentJson.Options);
    }

    /// <summary>Read one response, or <c>null</c> when the agent hung up.</summary>
    public static async Task<ControlResponse?> ReadResponseAsync(
        Stream stream, CancellationToken cancellationToken = default)
    {
        var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);

        if (line is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<ControlResponse>(line, AgentJson.Options);
    }

    public static Task WriteRequestAsync(
        Stream stream, ControlRequest request, CancellationToken cancellationToken = default)
        => WriteLineAsync(stream, JsonSerializer.Serialize(request, AgentJson.Options), cancellationToken);

    public static Task WriteResponseAsync(
        Stream stream, ControlResponse response, CancellationToken cancellationToken = default)
        => WriteLineAsync(stream, JsonSerializer.Serialize(response, AgentJson.Options), cancellationToken);

    private static async Task WriteLineAsync(
        Stream stream, string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");

        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadLineAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var line = new MemoryStream();

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                // End of stream. A partial line is a client that died
                // mid-sentence, which is not a message.
                return null;
            }

            if (buffer[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(line.ToArray());
            }

            line.WriteByte(buffer[0]);
        }
    }
}

/// <summary>One thing the local UI asked for.</summary>
/// <param name="Command">One of the command constants on <see cref="ControlProtocol"/>.</param>
/// <param name="Payload">Command-specific arguments, or <c>null</c>.</param>
public sealed record ControlRequest(string Command, JsonObject? Payload = null);

/// <summary>The agent's answer.</summary>
/// <param name="Ok">Whether the command did what was asked.</param>
/// <param name="Data">The answer, for commands that have one.</param>
/// <param name="Error">A stable code the UI can switch on, when <paramref name="Ok"/> is false.</param>
/// <param name="Detail">The sentence a technician reads.</param>
public sealed record ControlResponse(
    bool Ok,
    JsonObject? Data = null,
    string? Error = null,
    string? Detail = null)
{
    public static ControlResponse Success(JsonObject? data = null) => new(true, data);

    public static ControlResponse Failure(string error, string detail) =>
        new(false, null, error, detail);
}
