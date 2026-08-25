namespace AdlAgent.Windows.Platform;

/// <summary>
/// Stopping and starting the agent's own Windows service.
/// </summary>
/// <remarks>
/// Not the control surface, which is the running agent's conversation with a
/// tray; this is the Service Control Manager, and the only caller is
/// <see cref="SetUrl"/>. Nothing the agent does while it is running involves
/// stopping itself, so this is not one of the core's seams and does not
/// belong beside them.
/// <para>
/// Stop and start rather than one restart, because the order matters to what
/// sits between them. The service holds the token and the configuration cache
/// open and rewrites them on every sync; a repoint that cleared them under a
/// running service would race it, and lose. So the machine is stopped first,
/// changed while nothing is writing, and started again.
/// </para>
/// <para>
/// An interface because the alternative in a test is a suite that stops and
/// starts a real Windows service -- which the machine running it usually does
/// not have, and which the CI runner must not be asked to acquire.
/// </para>
/// </remarks>
public interface IServiceControl
{
    /// <summary>
    /// Stop the service and wait for it to be stopped.
    /// </summary>
    /// <remarks>
    /// A service that was not running, or is not installed at all, is not a
    /// failure: this verb must still work on a machine whose service has
    /// crashed, been disabled, or never been started. What it cannot do is
    /// pretend -- a stop that failed on a service that is genuinely running
    /// surfaces as the <see cref="StartAsync"/> that follows it refusing.
    /// </remarks>
    /// <exception cref="ServiceControlFailedException">
    /// The Service Control Manager could not be reached at all.
    /// </exception>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Start the service and wait for it to be running.</summary>
    /// <exception cref="ServiceControlFailedException">
    /// It did not start, with the reason as the tooling gave it.
    /// </exception>
    Task StartAsync(CancellationToken cancellationToken = default);
}

/// <summary>The service could not be stopped or started.</summary>
/// <remarks>
/// Carries a sentence rather than a code because the only reader is a
/// technician standing at the machine, and what they need is which thing to
/// do next.
/// </remarks>
public sealed class ServiceControlFailedException : Exception
{
    public ServiceControlFailedException(string message) : base(message)
    {
    }

    public ServiceControlFailedException(string message, Exception inner) : base(message, inner)
    {
    }
}
