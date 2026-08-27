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
    /// Ask ADL for this device's configuration now. No payload.
    /// </summary>
    /// <remarks>
    /// Started rather than performed: the answer is the attempt, not its
    /// outcome, and the outcome arrives on <see cref="StatusCommand"/>'s
    /// <c>requested_sync</c> a moment later. This surface serves one client at
    /// a time and times out in three seconds, so a command that waited for an
    /// HTTP call over these links would report a working service as absent.
    /// <para>
    /// Configuration only. It does not scan and does not upload -- that is
    /// <see cref="CollectCommand"/> -- because asking ADL what this machine is
    /// meant to be doing and asking this machine to do it are two different
    /// questions and two different waits.
    /// </para>
    /// </remarks>
    public const string SyncCommand = "sync";

    /// <summary>
    /// Run a cycle for one station now. Payload:
    /// <c>{"station_link_id": 11}</c>.
    /// </summary>
    /// <remarks>
    /// Started, for the same reason as <see cref="SyncCommand"/> and more so:
    /// a station with months of backlog uploads for minutes, and holding the
    /// only client slot for that long would freeze the tray's own status poll
    /// -- and with it the header, the next-step line and the colour of the
    /// icon in the corner -- for the duration.
    /// <para>
    /// Refused, in a sentence, when a cycle is already running: the station
    /// will be collected as part of it, so a queue would only start a second
    /// run minutes later against a window nobody still has open.
    /// </para>
    /// </remarks>
    public const string CollectCommand = "collect";

    /// <summary>
    /// What the collect in flight -- or the last one -- is doing. No payload.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="CollectCommand"/> returning at once. The
    /// window that asked for a collect asks this every second, in short
    /// round trips that leave the surface free between them.
    /// </remarks>
    public const string CollectStatusCommand = "collect_status";

    /// <summary>
    /// Stop the collect in flight. Payload: <c>{"station_link_id": 11}</c>.
    /// </summary>
    /// <remarks>
    /// The station is named so that a stale window cannot stop a run it is
    /// not the window for -- a technician who left one open, walked away, and
    /// came back to press Cancel after the scheduled cycle had started
    /// something else.
    /// <para>
    /// Nothing is repaired by cancelling. The agent keeps no record of what it
    /// delivered, so files a stopped run did not reach are offered again by
    /// the next cycle exactly as if it had never run.
    /// </para>
    /// </remarks>
    public const string CollectCancelCommand = "collect_cancel";

    /// <summary>
    /// The unit passes this machine has recorded, newest first. Payload:
    /// <c>{"station_link_id": 11, "most": 10}</c>, both optional.
    /// </summary>
    /// <remarks>
    /// The other half of the sentence the live probe answers. That probe
    /// exists to tell "scanned 0, no error" apart from a folder that is
    /// really empty, and it can only ever speak about this instant; this
    /// speaks about the passes that already happened, which is where a
    /// question about 13:24 is answered.
    /// <para>
    /// Named by station rather than by unit, because a station's unit is
    /// whatever it happens to share a folder with and nobody knows the name
    /// of that. Omitting the station gives the machine's own passes, which is
    /// what the diagnostics bundle takes.
    /// </para>
    /// </remarks>
    public const string PassesCommand = "passes";

    /// <summary>
    /// Write a plain-text diagnostics bundle. Payload: <c>{"path": "..."}</c>.
    /// </summary>
    /// <remarks>
    /// The path comes from the client and the file is written by the agent,
    /// which is the only arrangement that works on the service tier: the logs
    /// are in a folder whose permissions the MSI has replaced with SYSTEM and
    /// Administrators, so the tray cannot read them, and the service can. The
    /// technician picks where it goes and the service fills it.
    /// <para>
    /// The bundle rather than the bytes, for the same reason: it is far larger
    /// than <see cref="MaxMessageBytes"/> and this surface serves one client
    /// at a time.
    /// </para>
    /// </remarks>
    public const string DiagnosticsCommand = "diagnostics";

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
