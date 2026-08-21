using System.IO.Pipes;
using AdlAgent.Core.Control;
using AdlAgent.Core.Platform;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Windows.Platform;

/// <summary>
/// The control-surface seam on Windows: a named pipe.
/// </summary>
/// <remarks>
/// Carrying bytes is the whole of this class's job. It never learns what a
/// command means -- it reads a framed request, hands it to the core, writes
/// the framed answer back -- which is what lets the Linux head serve the same
/// conversation over a domain socket without a second implementation of
/// anything that matters.
/// <para>
/// One client at a time, on purpose: the only client is the tray, and a
/// backlog of local UI connections is not a problem this product has.
/// </para>
/// </remarks>
public sealed class NamedPipeControlSurface : IControlSurface
{
    /// <summary>
    /// The pipe the tray connects to. A fixed name, because a single agent
    /// runs per machine and the tray has to find it without configuration.
    /// </summary>
    public const string PipeName = "adl-agent";

    private readonly ILogger<NamedPipeControlSurface> _logger;

    public NamedPipeControlSurface(ILogger<NamedPipeControlSurface> logger)
    {
        _logger = logger;
    }

    public async Task ServeAsync(ControlRequestHandler handler, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Control surface listening on the {Pipe} pipe.", PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            // NOTE: the pipe is created with the default ACL, which is enough
            // while the only caller is an administrator running the agent
            // interactively. The tray ticket adds the explicit ACL that lets
            // a service running as LocalSystem be reached by the technician's
            // own logon session.
            using var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);

                await ServeClientAsync(pipe, handler, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException exception)
            {
                // The tray was closed mid-conversation. Perfectly normal;
                // wait for the next one.
                _logger.LogDebug(exception, "A control client disconnected.");
            }
        }
    }

    private static async Task ServeClientAsync(
        NamedPipeServerStream pipe, ControlRequestHandler handler, CancellationToken stoppingToken)
    {
        while (pipe.IsConnected && !stoppingToken.IsCancellationRequested)
        {
            var request = await ControlProtocol.ReadRequestAsync(pipe, stoppingToken)
                .ConfigureAwait(false);

            if (request is null)
            {
                return;
            }

            var response = await handler(request, stoppingToken).ConfigureAwait(false);

            await ControlProtocol.WriteResponseAsync(pipe, response, stoppingToken)
                .ConfigureAwait(false);
        }
    }
}
