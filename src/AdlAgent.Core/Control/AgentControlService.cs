using System.Text.Json;
using System.Text.Json.Nodes;
using AdlAgent.Core.Api;
using AdlAgent.Core.Configuration;
using AdlAgent.Core.Cycle;
using AdlAgent.Core.Diagnostics;
using AdlAgent.Core.Hosting;
using AdlAgent.Core.Pairing;
using AdlAgent.Core.Platform;
using AdlAgent.Core.Serialization;
using AdlAgent.Core.Status;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Control;

/// <summary>
/// Answers the local UI, over whatever transport the head provides.
/// </summary>
/// <remarks>
/// Every command means the same thing on every platform because it is
/// implemented once, here. The head's job stops at carrying bytes: a named
/// pipe on Windows, a domain socket on Linux, and no knowledge of what
/// "pair" does on either.
/// </remarks>
public sealed class AgentControlService : BackgroundService
{
    private readonly IControlSurface _surface;
    private readonly AgentSession _session;
    private readonly AgentStatusReader _status;
    private readonly AgentStationsReader _stations;
    private readonly ConfigurationService _configuration;
    private readonly StationLinkConfigWriter _writer;
    private readonly FolderPreview _preview;
    private readonly OnDemandSync _syncs;
    private readonly OnDemandCollect _collects;
    private readonly CycleLogReader _passes;
    private readonly DiagnosticsBundle _bundle;
    private readonly AgentWakeSignal _wake;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentControlService> _logger;

    public AgentControlService(
        IControlSurface surface,
        AgentSession session,
        AgentStatusReader status,
        AgentStationsReader stations,
        ConfigurationService configuration,
        StationLinkConfigWriter writer,
        FolderPreview preview,
        OnDemandSync syncs,
        OnDemandCollect collects,
        CycleLogReader passes,
        DiagnosticsBundle bundle,
        AgentWakeSignal wake,
        TimeProvider time,
        ILogger<AgentControlService> logger)
    {
        _surface = surface;
        _session = session;
        _status = status;
        _stations = stations;
        _configuration = configuration;
        _writer = writer;
        _preview = preview;
        _syncs = syncs;
        _collects = collects;
        _passes = passes;
        _bundle = bundle;
        _wake = wake;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _surface.ServeAsync(HandleAsync, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // The agent's real work is sending data, and it must not stop
            // because the thing a technician looks at fell over. They will
            // find out when the tray does not answer.
            _logger.LogError(exception, "The control surface stopped serving.");
        }
    }

    /// <summary>Do what one request asked, and say what happened.</summary>
    public async Task<ControlResponse> HandleAsync(
        ControlRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return request.Command switch
            {
                ControlProtocol.StatusCommand => Status(),
                ControlProtocol.PairCommand => await PairAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                ControlProtocol.StationsCommand => Stations(),
                ControlProtocol.PreviewCommand => Preview(request),
                ControlProtocol.ConfigureCommand => await ConfigureAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                ControlProtocol.SyncCommand => Sync(),
                ControlProtocol.CollectCommand => Collect(request),
                ControlProtocol.CollectStatusCommand => CollectStatus(),
                ControlProtocol.CollectCancelCommand => CollectCancel(request),
                ControlProtocol.PassesCommand => Passes(request),
                ControlProtocol.DiagnosticsCommand => await DiagnosticsAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                _ => ControlResponse.Failure(
                    "unknown_command",
                    $"This agent does not know the command '{request.Command}'."),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Control command {Command} failed.", request.Command);

            return ControlResponse.Failure("agent_error", exception.Message);
        }
    }

    private ControlResponse Status() => ControlResponse.Success(ToJson(_status.Read()));

    private async Task<ControlResponse> PairAsync(
        ControlRequest request, CancellationToken cancellationToken)
    {
        var code = request.Payload?["pairing_code"]?.GetValue<string>();

        try
        {
            await _session.PairAsync(code ?? "", cancellationToken).ConfigureAwait(false);
        }
        catch (AdlRequestException exception)
        {
            return ControlResponse.Failure(exception.Code, exception.Detail);
        }
        catch (AdlUnreachableException exception)
        {
            return ControlResponse.Failure("adl_unreachable", exception.Message);
        }

        // The technician is watching the fleet view right now. Waiting out the
        // cadence before the first sync and the first beat would make a
        // working install look like a failed one.
        _wake.Set();

        return ControlResponse.Success(ToJson(_status.Read()));
    }

    private ControlResponse Stations() => ControlResponse.Success(ToJson(_stations.Read()));

    /// <summary>
    /// Ask ADL for this device's configuration now, and answer with the
    /// attempt rather than its outcome.
    /// </summary>
    /// <remarks>
    /// The outcome arrives on the status the UI already polls. What this
    /// answer carries is the moment the attempt started, which is what lets a
    /// window tell its own press from the one before it and from a sync the
    /// cycle happened to run at the same time.
    /// </remarks>
    private ControlResponse Sync() => ControlResponse.Success(ToJson(_syncs.Start()));

    /// <summary>Run a cycle for one station now, or say why not.</summary>
    /// <remarks>
    /// A refusal is a <c>collect_refused</c> rather than an <c>Ok</c> with an
    /// empty body, so a UI can tell "it is running, watch it" from "it is not,
    /// and here is the sentence to show". None of the reasons is a code
    /// anything switches on -- switched off in ADL, no folder bound, a cycle
    /// already running -- so the sentence is the whole of the answer.
    /// </remarks>
    private ControlResponse Collect(ControlRequest request)
    {
        if (StationLinkId(request.Payload) is not { } stationLinkId)
        {
            return ControlResponse.Failure(
                "invalid_request",
                "Say which station link to collect: {\"station_link_id\": 11}.");
        }

        var started = _collects.Start(stationLinkId);

        return started.Ok
            ? ControlResponse.Success(ToJson(started.Progress!))
            : ControlResponse.Failure("collect_refused", started.Refusal!);
    }

    /// <summary>
    /// What the collect in flight -- or the last one -- is doing.
    /// </summary>
    /// <remarks>
    /// The last one and not nothing, because the window asking is the one that
    /// has to show how it ended: a poll landing a moment after the final file
    /// would otherwise be told there was no run, on the screen somebody is
    /// watching for the answer.
    /// </remarks>
    private ControlResponse CollectStatus() => _collects.Progress is { } progress
        ? ControlResponse.Success(ToJson(progress))
        : ControlResponse.Failure(
            "no_collect", "No collect has been asked for on this machine since it started.");

    private ControlResponse CollectCancel(ControlRequest request)
    {
        if (StationLinkId(request.Payload) is not { } stationLinkId)
        {
            return ControlResponse.Failure(
                "invalid_request",
                "Say which station link's collect to stop: {\"station_link_id\": 11}.");
        }

        // Named rather than "the one running", so a window somebody left open
        // cannot stop a run started for a different station after they walked
        // away.
        if (!_collects.Cancel(stationLinkId))
        {
            return ControlResponse.Failure(
                "no_collect", "There is no collect running for that station to stop.");
        }

        return ControlResponse.Success(ToJson(_collects.Progress!));
    }

    /// <summary>
    /// The unit passes this machine has recorded, newest first.
    /// </summary>
    /// <remarks>
    /// Trimmed to fit one control message rather than truncated per record.
    /// A record is one unit's whole story and half of one is worse than none:
    /// a reader shown a pass with its file detail cut off has no way to tell
    /// that from a pass in which nothing happened. So whole passes are dropped
    /// from the oldest end until the answer fits, and the answer says that it
    /// was.
    /// </remarks>
    private ControlResponse Passes(ControlRequest request)
    {
        var most = Math.Clamp(Whole(request.Payload, "most") ?? DefaultPasses, 1, MostPasses);

        // One more than was asked for, so that "there are older ones" is
        // something this knows rather than something it infers from having
        // filled the answer exactly.
        var found = _passes.Recent(most + 1, StationLinkId(request.Payload));
        var more = found.Count > most;
        var passes = found.Take(most).ToList();

        while (passes.Count > 1 && Size(passes, more) > PassesBudget)
        {
            passes.RemoveAt(passes.Count - 1);
            more = true;
        }

        return ControlResponse.Success(ToJson(new CyclePasses
        {
            Passes = passes,
            More = more,
        }));
    }

    /// <summary>How many passes a UI gets when it does not say.</summary>
    private const int DefaultPasses = 10;

    /// <summary>The most it may ask for, whatever it says.</summary>
    private const int MostPasses = 25;

    /// <summary>
    /// How much of one control message the passes may take.
    /// </summary>
    /// <remarks>
    /// Below <see cref="ControlProtocol.MaxMessageBytes"/> with room over for
    /// the envelope around it, because the reader at the other end refuses a
    /// line longer than that cap and a refusal here would be a window that
    /// shows nothing at all on exactly the busiest machine.
    /// </remarks>
    private const int PassesBudget = ControlProtocol.MaxMessageBytes - 4096;

    private static int Size(IReadOnlyList<CycleRecord> passes, bool more) =>
        JsonSerializer.Serialize(
            new CyclePasses { Passes = passes, More = more }, AgentJson.Options).Length;

    /// <summary>
    /// Write a plain-text diagnostics bundle where the client asked.
    /// </summary>
    /// <remarks>
    /// Performed rather than started, unlike sync and collect. This one is
    /// bounded -- it is a few hundred kilobytes off local files -- and the
    /// window that asked has a Save dialog open behind it and nothing to draw
    /// until it knows whether the file was written.
    /// </remarks>
    private async Task<ControlResponse> DiagnosticsAsync(
        ControlRequest request, CancellationToken cancellationToken)
    {
        if (request.Payload?["path"]?.GetValue<string>() is not { } path ||
            string.IsNullOrWhiteSpace(path))
        {
            return ControlResponse.Failure(
                "invalid_request",
                "Say where the bundle should be written: {\"path\": \"C:\\\\Temp\\\\adl-agent.txt\"}.");
        }

        try
        {
            var bytes = await _bundle.WriteToAsync(path, cancellationToken).ConfigureAwait(false);

            return ControlResponse.Success(new JsonObject
            {
                ["path"] = path,
                ["bytes"] = bytes,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            // The service runs as SYSTEM and the path came from somebody's
            // Save dialog, so this is a real case: a mapped drive the service
            // account does not have, or a folder it may not write.
            return ControlResponse.Failure(
                "diagnostics_failed", $"The agent could not write {path}: {exception.Message}");
        }
    }

    /// <summary>
    /// Count what these settings would match, without writing any of them.
    /// </summary>
    /// <remarks>
    /// Read-only on purpose, and the only command that is. A technician
    /// trying a pattern is exploring; nothing they type here reaches ADL
    /// until they say so with <see cref="ControlProtocol.ConfigureCommand"/>.
    /// </remarks>
    private ControlResponse Preview(ControlRequest request)
    {
        var payload = request.Payload ?? [];
        var stationLinkId = StationLinkId(payload);
        var settings = new JsonObject();

        // The named station's settings are the ground the typed ones are laid
        // over. That overlay is what makes the count live: a window sends the
        // one box somebody is editing and gets an answer for the whole
        // configuration as it would then stand, rather than having to hold --
        // and keep in step -- a copy of every other setting ADL sent.
        // The station's own timezone, which is HQ's tier and so never
        // something the window can type: a dated folder tree is carved in it,
        // so a preview that guessed at UTC would count the wrong folder for
        // every station outside it.
        string? timezoneId = null;

        if (stationLinkId is not null)
        {
            var link = _configuration.Current?.StationLinks
                .FirstOrDefault(candidate => candidate.Id == stationLinkId);

            if (link is null)
            {
                return ControlResponse.Failure(
                    "unknown_station_link",
                    UnknownStationLinkException.Describe(stationLinkId.Value));
            }

            settings = ToJson(link.Config);
            timezoneId = link.Admin.Timezone;
        }

        foreach (var typed in payload)
        {
            if (typed.Key != "station_link_id")
            {
                settings[typed.Key] = typed.Value?.DeepClone();
            }
        }

        StationLinkAppConfig config;

        try
        {
            config = settings.Deserialize<StationLinkAppConfig>(AgentJson.Options)
                ?? new StationLinkAppConfig();
        }
        catch (JsonException exception)
        {
            return ControlResponse.Failure("invalid_request", exception.Message);
        }

        return ControlResponse.Success(ToJson(_preview.Preview(config, timezoneId, _time.GetUtcNow())));
    }

    private async Task<ControlResponse> ConfigureAsync(
        ControlRequest request, CancellationToken cancellationToken)
    {
        var stationLinkId = StationLinkId(request.Payload);

        if (stationLinkId is null || request.Payload?["config"] is not JsonObject changes)
        {
            return ControlResponse.Failure(
                "invalid_request",
                "Say which station link to configure and what to change: "
                + "{\"station_link_id\": 11, \"config\": {...}}.");
        }

        try
        {
            var written = await _writer
                .WriteAsync(stationLinkId.Value, changes, cancellationToken)
                .ConfigureAwait(false);

            return ControlResponse.Success(ToJson(written));
        }
        catch (NotPairedException exception)
        {
            return ControlResponse.Failure("not_paired", exception.Message);
        }
        catch (UnknownStationLinkException exception)
        {
            return ControlResponse.Failure("unknown_station_link", exception.Message);
        }
        catch (DeviceRevokedException exception)
        {
            // Not passed through as ADL's own code: what the technician has
            // to do about a revoked token is the same whatever ADL called it,
            // and it is the one refusal the tray turns into an instruction.
            return ControlResponse.Failure(ControlProtocol.RePairNeededError, exception.Detail);
        }
        catch (AdlRequestException exception)
        {
            return ControlResponse.Failure(exception.Code, exception.Detail);
        }
        catch (AdlUnreachableException exception)
        {
            return ControlResponse.Failure("adl_unreachable", exception.Message);
        }
    }

    /// <summary>
    /// The station link a command names, however the client spelled the
    /// number.
    /// </summary>
    /// <remarks>
    /// Two attempts because a <see cref="JsonValue"/> only converts to the
    /// type it is actually holding, and the two clients of this protocol
    /// hold it differently: a request that arrived over the transport was
    /// parsed from text and is a JSON number, while one built in process --
    /// by a test, or by a head that skips the wire -- is whatever C# literal
    /// was written. A protocol that answered "unknown command argument" to
    /// one of those and not the other would be a trap.
    /// </remarks>
    private static long? StationLinkId(JsonObject? payload)
    {
        if (payload?["station_link_id"] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var id))
        {
            return id;
        }

        return value.TryGetValue<int>(out var narrower) ? narrower : null;
    }

    /// <summary>A plain number a command carries, however the client spelled it.</summary>
    private static int? Whole(JsonObject? payload, string name)
    {
        if (payload?[name] is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<int>(out var whole) ? whole : null;
    }

    private static JsonObject ToJson<T>(T value) =>
        JsonSerializer.SerializeToNode(value, AgentJson.Options)?.AsObject() ?? [];
}
