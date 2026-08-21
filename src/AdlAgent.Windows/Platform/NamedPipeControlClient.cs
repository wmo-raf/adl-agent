using System.IO.Pipes;
using AdlAgent.Core.Control;

namespace AdlAgent.Windows.Platform;

/// <summary>
/// The other end of <see cref="NamedPipeControlSurface"/>.
/// </summary>
/// <remarks>
/// Used by the <c>pair</c> and <c>status</c> verbs now and by the WPF tray
/// when it lands. It knows how to reach the service and nothing else -- what
/// the commands mean is the core's, and stays there.
/// </remarks>
public sealed class NamedPipeControlClient
{
    private readonly TimeSpan _connectTimeout;
    private readonly string _pipeName;

    public NamedPipeControlClient(TimeSpan? connectTimeout = null, string? pipeName = null)
    {
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _pipeName = pipeName ?? NamedPipeControlSurface.PipeName;
    }

    /// <summary>
    /// Ask the running agent one thing.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// Nothing is listening -- the service is not running, or is running as
    /// an account this caller cannot reach.
    /// </exception>
    public async Task<ControlResponse> AskAsync(
        ControlRequest request, CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await pipe.ConnectAsync((int)_connectTimeout.TotalMilliseconds, cancellationToken)
            .ConfigureAwait(false);

        return await ControlProtocol.AskAsync(pipe, request, cancellationToken).ConfigureAwait(false);
    }
}
