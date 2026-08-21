using System.Text.Json;
using System.Text.Json.Nodes;
using AdlAgent.Core.Api;
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
    private readonly AgentWakeSignal _wake;
    private readonly ILogger<AgentControlService> _logger;

    public AgentControlService(
        IControlSurface surface,
        AgentSession session,
        AgentStatusReader status,
        AgentWakeSignal wake,
        ILogger<AgentControlService> logger)
    {
        _surface = surface;
        _session = session;
        _status = status;
        _wake = wake;
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

    private static JsonObject ToJson<T>(T value) =>
        JsonSerializer.SerializeToNode(value, AgentJson.Options)?.AsObject() ?? [];
}
