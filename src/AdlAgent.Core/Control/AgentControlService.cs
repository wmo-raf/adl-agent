using System.Text.Json;
using System.Text.Json.Nodes;
using AdlAgent.Core.Api;
using AdlAgent.Core.Configuration;
using AdlAgent.Core.Cycle;
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

        return ControlResponse.Success(ToJson(_preview.Preview(config, _time.GetUtcNow())));
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

    private static JsonObject ToJson<T>(T value) =>
        JsonSerializer.SerializeToNode(value, AgentJson.Options)?.AsObject() ?? [];
}
