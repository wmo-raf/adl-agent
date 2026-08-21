using AdlAgent.Core.Control;

namespace AdlAgent.Core.Platform;

/// <summary>
/// Platform seam 4 of 4: the transport the local UI talks to the agent over.
/// </summary>
/// <remarks>
/// The protocol is not part of this seam -- it is defined once in
/// <see cref="AdlAgent.Core.Control"/> and is the same protocol on every
/// platform. What differs is only what it is carried on: a named pipe with a
/// Windows ACL, a unix domain socket with file permissions. An implementation
/// accepts connections, reads framed requests, hands each to
/// <paramref name="handler"/>, and writes back the framed response.
/// </remarks>
public interface IControlSurface
{
    /// <summary>
    /// Serve local clients until <paramref name="stoppingToken"/> is cancelled.
    /// </summary>
    Task ServeAsync(ControlRequestHandler handler, CancellationToken stoppingToken);
}

/// <summary>
/// What a control surface calls for each request it receives. Supplied by the
/// core, so a head never has to know what any command means.
/// </summary>
public delegate Task<ControlResponse> ControlRequestHandler(
    ControlRequest request,
    CancellationToken cancellationToken);
