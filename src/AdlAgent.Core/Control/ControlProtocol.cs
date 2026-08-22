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
    /// <summary>
    /// The longest line either end will read.
    /// </summary>
    /// <remarks>
    /// A cap because the control surface serves one client at a time: a local
    /// process that connects and then never sends a newline would otherwise
    /// grow a buffer without limit and hold the only slot, and the technician
    /// would find a tray that never answers on a service that is working
    /// perfectly. Far larger than any real message.
    /// </remarks>
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>Ask what the agent is doing. No payload.</summary>
    public const string StatusCommand = "status";

    /// <summary>Redeem a pairing code. Payload: <c>{"pairing_code": "..."}</c>.</summary>
    public const string PairCommand = "pair";

    /// <summary>
    /// Every station ADL has linked to this machine, with its local binding
    /// and what the last cycle did for it. No payload.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="StatusCommand"/> because the two are drawn at
    /// different rates and cost different amounts: the tray polls the status
    /// line every few seconds and reads the station list when a technician
    /// opens the window.
    /// </remarks>
    public const string StationsCommand = "stations";

    /// <summary>
    /// Count what a folder and a pattern would match, without saving either.
    /// </summary>
    /// <remarks>
    /// Payload is a station link's app-tier settings, any subset of them:
    /// <c>{"station_link_id": 11, "file_pattern": "GARISSA_*.dat"}</c> reads
    /// the rest from what ADL holds for that station, and a payload with no
    /// <c>station_link_id</c> is previewed exactly as it was given. Story 7,
    /// and the reason a pattern typed at the machine is right before it is
    /// saved rather than a day later.
    /// </remarks>
    public const string PreviewCommand = "preview";

    /// <summary>
    /// Write a station's app-tier settings through to ADL. Payload:
    /// <c>{"station_link_id": 11, "config": {...}}</c>.
    /// </summary>
    /// <remarks>
    /// Through to ADL, and nowhere else. ADL is the single source of truth
    /// for durable configuration (decision #260), so this command is a
    /// write-through and never a local override: a write ADL did not accept
    /// did not happen.
    /// </remarks>
    public const string ConfigureCommand = "configure";

    /// <summary>
    /// The agent accepted the connection and then closed it without saying
    /// anything.
    /// </summary>
    /// <remarks>
    /// Not a refusal, despite arriving as one: it means the conversation was
    /// lost rather than that anything was decided. The surface serves one
    /// client at a time and lets go of its pipe between clients, so a client
    /// that connected in the instant before that happens is dropped without
    /// an answer -- as is one that connected while the service was stopping.
    /// A caller that can ask again should, which is why the code is named
    /// here rather than written twice.
    /// </remarks>
    public const string NoAnswerError = "no_answer";

    /// <summary>
    /// ADL has revoked this machine's token, and the only thing to do about
    /// it is pair again.
    /// </summary>
    /// <remarks>
    /// Named because it is the one refusal a local UI switches on rather
    /// than merely shows: every other error code this surface produces is
    /// read by a person, but this one turns a window's message into an
    /// instruction, and the two ends must agree on the spelling.
    /// </remarks>
    public const string RePairNeededError = "re_pair_needed";

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

    /// <summary>
    /// Say one thing and wait for the answer -- the client half of the
    /// conversation, in one call.
    /// </summary>
    /// <remarks>
    /// Here rather than in either head because both of them need it and it is
    /// the same code: the tray, the Linux CLI, and the <c>adl-agent pair</c>
    /// verb are three clients of one protocol, and a second implementation is
    /// a second thing that can drift.
    /// </remarks>
    public static async Task<ControlResponse> AskAsync(
        Stream stream, ControlRequest request, CancellationToken cancellationToken = default)
    {
        await WriteRequestAsync(stream, request, cancellationToken).ConfigureAwait(false);

        var response = await ReadResponseAsync(stream, cancellationToken).ConfigureAwait(false);

        return response ?? Failure(
            NoAnswerError, "The agent closed the connection without answering.");
    }

    private static ControlResponse Failure(string error, string detail) =>
        ControlResponse.Failure(error, detail);

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

            if (line.Length >= MaxMessageBytes)
            {
                throw new InvalidDataException(
                    $"A control message longer than {MaxMessageBytes} bytes arrived without a newline.");
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
