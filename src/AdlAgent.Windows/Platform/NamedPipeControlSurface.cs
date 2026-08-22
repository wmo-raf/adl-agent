using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
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
    private readonly string _pipeName;

    /// <param name="pipeName">
    /// Overridable so a test can serve on a name of its own. A machine runs
    /// one agent, so the default is the only name that is ever used in the
    /// field.
    /// </param>
    public NamedPipeControlSurface(
        ILogger<NamedPipeControlSurface> logger, string? pipeName = null)
    {
        _logger = logger;
        _pipeName = pipeName ?? PipeName;
    }

    public async Task ServeAsync(ControlRequestHandler handler, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Control surface listening on the {Pipe} pipe.", _pipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var pipe = Listen();

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

    /// <summary>
    /// A fresh instance of the pipe, with the ACL the tray needs to reach it.
    /// </summary>
    /// <remarks>
    /// The default ACL is not enough here, and the reason is the whole point
    /// of the service tier: the agent runs as LocalSystem so that data keeps
    /// flowing with nobody logged on (story 26), and a pipe created by
    /// LocalSystem under the default ACL is one a technician's own logon
    /// session cannot open. Without this the tray answers "the service is not
    /// answering" on a machine where the service is working perfectly --
    /// which is the single worst message this product could show, because it
    /// is the one that starts a phone call to another country.
    /// <para>
    /// Off Windows this asks for no ACL at all. There, the pipe is a unix
    /// domain socket under the process's own temporary directory, its file
    /// permissions are the access control, and a Windows security descriptor
    /// is not something the platform could be handed.
    /// </para>
    /// </remarks>
    private NamedPipeServerStream Listen()
    {
        if (OperatingSystem.IsWindows())
        {
            return NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                LocalUiSecurity());
        }

        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    /// <summary>
    /// Who may talk to the agent over this machine's pipe.
    /// </summary>
    /// <remarks>
    /// Three allows and one deny, and the deny is the one worth explaining.
    /// Windows publishes named pipes over SMB through the <c>IPC$</c> share,
    /// so a pipe that says nothing about the network is reachable from it by
    /// anyone the machine would authenticate -- and this pipe can pair the
    /// device and rewrite where a station's data is read from. These servers
    /// sit on ministry networks with a great deal else on them. The tray runs
    /// on the machine itself, so nothing is lost by saying so.
    /// <para>
    /// Interactive rather than Users: the technician is logged on at the
    /// console or over RDP, which is what Interactive means, while Users
    /// would also cover every service account on the box.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static PipeSecurity LocalUiSecurity()
    {
        var security = new PipeSecurity();

        // The service's own account, and whoever administers the machine.
        Allow(security, WellKnownSidType.LocalSystemSid, PipeAccessRights.FullControl);
        Allow(security, WellKnownSidType.BuiltinAdministratorsSid, PipeAccessRights.FullControl);

        // The technician's logon session: enough to hold the conversation,
        // and nothing that would let it change who else may.
        Allow(
            security,
            WellKnownSidType.InteractiveSid,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize);

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));

        return security;
    }

    [SupportedOSPlatform("windows")]
    private static void Allow(PipeSecurity security, WellKnownSidType who, PipeAccessRights rights) =>
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(who, null), rights, AccessControlType.Allow));

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
