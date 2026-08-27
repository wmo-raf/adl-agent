using System.Text.Json;
using System.Text.Json.Nodes;
using AdlAgent.Core.Api;
using AdlAgent.Core.Configuration;
using AdlAgent.Core.Control;
using AdlAgent.Core.Cycle;
using AdlAgent.Core.Diagnostics;
using AdlAgent.Core.Serialization;
using AdlAgent.Core.Status;

namespace AdlAgent.Windows.Platform;

/// <summary>
/// The whole of what a local UI can ask the agent, typed.
/// </summary>
/// <remarks>
/// <see cref="NamedPipeControlClient"/> carries bytes; this turns them into
/// the questions a technician's window or a terminal actually asks, and
/// turns the answers back into the core's own snapshot records. It sits in
/// the head because both of the head's local UIs use it -- the
/// <c>adl-agent</c> verbs and the WPF tray -- and because it is where the
/// pipe is.
/// <para>
/// Deserialising into the core's records rather than into shapes declared
/// here is deliberate: a field added to the status answer appears in both
/// UIs without anyone remembering to declare it twice, and a field renamed
/// breaks the build rather than the display.
/// </para>
/// <para>
/// Nothing here decides anything. There is no cache and no local setting:
/// the tray is thin because its only route to any fact is a command the
/// service implements, so a UI showing something the service does not know
/// is not a state this program can reach. The one exception is the second
/// attempt below, which is about the transport rather than about any
/// answer.
/// </para>
/// </remarks>
public sealed class AgentControlLink
{
    /// <summary>
    /// How long to wait for the service before saying it is not there.
    /// </summary>
    /// <remarks>
    /// Short, because a UI asks on a timer and a technician watching a window
    /// that has stopped repainting decides the tray is broken well before a
    /// longer timeout would pay off. The pipe is on this machine: it answers
    /// at once or it is not listening.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long to leave between the two attempts a question gets.
    /// </summary>
    /// <remarks>
    /// The surface serves one client at a time and opens a fresh pipe
    /// instance after each, so there is a short instant between two
    /// conversations when nothing is listening. A window that redraws itself
    /// from four commands in a row would otherwise report a working service
    /// as absent now and then -- which is worse than a slow answer, because
    /// it is the message that starts a phone call.
    /// </remarks>
    private static readonly TimeSpan BetweenAttempts = TimeSpan.FromMilliseconds(150);

    private readonly Func<NamedPipeControlClient> _clients;

    /// <param name="clients">
    /// How to reach the running agent. Supplied by tests, and by the CLI when
    /// it is pointed at a pipe of its own, so neither depends on whether this
    /// machine happens to have an agent installed.
    /// </param>
    public AgentControlLink(Func<NamedPipeControlClient>? clients = null)
    {
        _clients = clients ?? (static () => new NamedPipeControlClient(DefaultTimeout));
    }

    /// <summary>What the agent is doing, as the header and the status view draw it.</summary>
    public Task<AgentAnswer<AgentStatusSnapshot>> StatusAsync(CancellationToken cancellationToken = default) =>
        AskAsync<AgentStatusSnapshot>(new ControlRequest(ControlProtocol.StatusCommand), cancellationToken);

    /// <summary>Redeem a pairing code. The answer is the status that now stands.</summary>
    public Task<AgentAnswer<AgentStatusSnapshot>> PairAsync(
        string pairingCode, CancellationToken cancellationToken = default) =>
        AskAsync<AgentStatusSnapshot>(
            new ControlRequest(
                ControlProtocol.PairCommand,
                new JsonObject { ["pairing_code"] = pairingCode }),
            cancellationToken);

    /// <summary>Every station ADL has linked to this machine.</summary>
    public Task<AgentAnswer<AgentStationsSnapshot>> StationsAsync(
        CancellationToken cancellationToken = default) =>
        AskAsync<AgentStationsSnapshot>(
            new ControlRequest(ControlProtocol.StationsCommand), cancellationToken);

    /// <summary>
    /// Count what these settings would match, without saving any of them.
    /// </summary>
    /// <param name="settings">
    /// Any subset of a station's app tier. With a <c>station_link_id</c> in
    /// it, the rest is read from what ADL holds -- so a UI sends the boxes
    /// somebody is editing and nothing else.
    /// </param>
    public Task<AgentAnswer<FolderPreviewResult>> PreviewAsync(
        JsonObject settings, CancellationToken cancellationToken = default) =>
        AskAsync<FolderPreviewResult>(
            new ControlRequest(ControlProtocol.PreviewCommand, settings), cancellationToken);

    /// <summary>
    /// Ask ADL for this device's configuration now.
    /// </summary>
    /// <remarks>
    /// The answer is the attempt rather than its outcome: the agent starts the
    /// call and returns, and what came of it arrives on the next
    /// <see cref="StatusAsync"/> as <c>RequestedSync</c>. A UI presses this and
    /// then watches the status it is already polling.
    /// </remarks>
    public Task<AgentAnswer<SyncAttempt>> SyncAsync(CancellationToken cancellationToken = default) =>
        AskAsync<SyncAttempt>(new ControlRequest(ControlProtocol.SyncCommand), cancellationToken);

    /// <summary>Run a cycle for one station now.</summary>
    /// <remarks>
    /// Answers as soon as the run is under way. A refusal -- a cycle already
    /// running, a station switched off in ADL, a station with no folder bound
    /// -- comes back as a refused answer whose detail is the sentence to show.
    /// </remarks>
    public Task<AgentAnswer<CollectProgress>> CollectAsync(
        long stationLinkId, CancellationToken cancellationToken = default) =>
        AskAsync<CollectProgress>(
            new ControlRequest(
                ControlProtocol.CollectCommand,
                new JsonObject { ["station_link_id"] = stationLinkId }),
            cancellationToken);

    /// <summary>What the collect in flight -- or the last one -- is doing.</summary>
    public Task<AgentAnswer<CollectProgress>> CollectStatusAsync(
        CancellationToken cancellationToken = default) =>
        AskAsync<CollectProgress>(
            new ControlRequest(ControlProtocol.CollectStatusCommand), cancellationToken);

    /// <summary>Stop the collect running for this station.</summary>
    public Task<AgentAnswer<CollectProgress>> CancelCollectAsync(
        long stationLinkId, CancellationToken cancellationToken = default) =>
        AskAsync<CollectProgress>(
            new ControlRequest(
                ControlProtocol.CollectCancelCommand,
                new JsonObject { ["station_link_id"] = stationLinkId }),
            cancellationToken);

    /// <summary>
    /// The unit passes this machine has recorded, newest first.
    /// </summary>
    /// <param name="stationLinkId">
    /// Only the passes this station was in, or <c>null</c> for the machine's
    /// own.
    /// </param>
    public Task<AgentAnswer<CyclePasses>> PassesAsync(
        long? stationLinkId = null, int? most = null, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject();

        if (stationLinkId is not null)
        {
            payload["station_link_id"] = stationLinkId.Value;
        }

        if (most is not null)
        {
            payload["most"] = most.Value;
        }

        return AskAsync<CyclePasses>(
            new ControlRequest(ControlProtocol.PassesCommand, payload), cancellationToken);
    }

    /// <summary>
    /// Have the agent write a plain-text diagnostics bundle at this path.
    /// </summary>
    /// <remarks>
    /// The agent writes it rather than the caller, because on the service tier
    /// the caller cannot read the logs: the state folder's permissions are
    /// SYSTEM and Administrators, and the tray runs as whoever is logged in.
    /// The path is the technician's choice and the bytes are the service's.
    /// </remarks>
    public Task<AgentAnswer<DiagnosticsWritten>> SaveDiagnosticsAsync(
        string path, CancellationToken cancellationToken = default) =>
        AskAsync<DiagnosticsWritten>(
            new ControlRequest(
                ControlProtocol.DiagnosticsCommand,
                new JsonObject { ["path"] = path }),
            cancellationToken);

    /// <summary>Write a station's app tier through the service to ADL.</summary>
    public Task<AgentAnswer<ConfigWriteResponse>> ConfigureAsync(
        long stationLinkId, JsonObject config, CancellationToken cancellationToken = default) =>
        AskAsync<ConfigWriteResponse>(
            new ControlRequest(
                ControlProtocol.ConfigureCommand,
                new JsonObject { ["station_link_id"] = stationLinkId, ["config"] = config }),
            cancellationToken);

    private async Task<AgentAnswer<T>> AskAsync<T>(
        ControlRequest request, CancellationToken cancellationToken) where T : class
    {
        var response = await ConnectAndAskAsync(request, cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            await Task.Delay(BetweenAttempts, cancellationToken).ConfigureAwait(false);

            response = await ConnectAndAskAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (response is null)
        {
            // The commonest thing that is wrong, and the only one a local UI
            // can do nothing about. Said as a sentence rather than left as an
            // exception, because the person reading it is a station
            // technician and the answer is "start the service".
            return AgentAnswer<T>.Unavailable(
                "The ADL Agent service is not answering. Check that it is running on this machine.");
        }

        if (!response.Ok)
        {
            return AgentAnswer<T>.Refused(
                response.Error ?? "agent_error",
                response.Detail ?? "The agent refused that.");
        }

        try
        {
            var value = response.Data?.Deserialize<T>(AgentJson.Options);

            return value is null
                ? AgentAnswer<T>.Refused("empty_answer", "The agent answered with nothing.")
                : AgentAnswer<T>.Answered(value);
        }
        catch (JsonException exception)
        {
            // A service older or newer than the UI asking. Worth a sentence
            // naming the cause, because the fix is to update one of them.
            return AgentAnswer<T>.Refused(
                "unreadable_answer",
                $"This window could not read the agent's answer ({exception.Message}). "
                + "The tray and the service may be different versions.");
        }
    }

    /// <summary>
    /// One attempt, or <c>null</c> when the agent could not be reached at
    /// all. A refusal is an answer and comes back as one.
    /// </summary>
    private async Task<ControlResponse?> ConnectAndAskAsync(
        ControlRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _clients().AskAsync(request, cancellationToken).ConfigureAwait(false);

            // A connection that was accepted and then dropped is the same
            // event as one that was never accepted -- the agent decided
            // nothing either way -- and it is the likelier half of the race
            // this retries for: the client reaches the pipe just as the
            // surface lets go of it.
            return response.Error == ControlProtocol.NoAnswerError ? null : response;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Where the agent put the bundle, and how big it is.</summary>
/// <remarks>
/// The size is worth carrying: the window says it, and "written, 412 KB" is
/// what tells a technician the file they are about to attach has something in
/// it.
/// </remarks>
public sealed record DiagnosticsWritten
{
    public string Path { get; init; } = "";

    public long Bytes { get; init; }
}

/// <summary>
/// One answer from the agent: what it said, or why there is nothing to show.
/// </summary>
/// <remarks>
/// A result type rather than exceptions, because all three outcomes are
/// things a UI draws rather than things that go wrong. A service that is not
/// running, a pairing code that has expired, and a station list are all
/// ordinary Tuesday for this program.
/// </remarks>
public sealed record AgentAnswer<T> where T : class
{
    private AgentAnswer(T? value, string? error, string? detail, bool serviceReached)
    {
        Value = value;
        Error = error;
        Detail = detail;
        ServiceReached = serviceReached;
    }

    public T? Value { get; }

    /// <summary>The stable code, when the agent refused. Null when it did not.</summary>
    public string? Error { get; }

    /// <summary>The sentence to show. Null when there is nothing to say.</summary>
    public string? Detail { get; }

    /// <summary>False when the service could not be reached at all.</summary>
    public bool ServiceReached { get; }

    public bool Ok => Value is not null;

    /// <summary>
    /// True when ADL has revoked this machine's token: the one refusal a UI
    /// turns into an instruction rather than a message.
    /// </summary>
    public bool NeedsRePairing => Error == ControlProtocol.RePairNeededError;

    public static AgentAnswer<T> Answered(T value) => new(value, null, null, serviceReached: true);

    public static AgentAnswer<T> Refused(string error, string detail) =>
        new(null, error, detail, serviceReached: true);

    public static AgentAnswer<T> Unavailable(string detail) =>
        new(null, "service_unavailable", detail, serviceReached: false);
}
