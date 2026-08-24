namespace AdlAgent.Core.Platform;

/// <summary>
/// Platform seam 3 of 5: what the process is running inside.
/// </summary>
/// <remarks>
/// Registering as a Windows Service or a systemd unit is the head's own job
/// and happens in its composition root -- one call to the framework, nothing
/// the core could usefully abstract. What the core does need from the host is
/// the handful of facts that differ per platform and would otherwise be
/// guessed: how it should describe the machine to ADL, how long it has been
/// up, and above all where durable state may be written, which is
/// <c>%ProgramData%</c> on Windows and <c>/var/lib</c> on Linux and must
/// never be a path the core made up.
/// </remarks>
public interface IHostLifecycle
{
    /// <summary>
    /// The machine, as it should appear in the fleet listing -- e.g.
    /// "Microsoft Windows 10.0.20348". Reported on every heartbeat.
    /// </summary>
    string PlatformDescription { get; }

    /// <summary>When this process started. Uptime is measured from it.</summary>
    DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Directory for state that must survive a restart: the device token and
    /// the offline configuration cache. Created by the head if it is missing,
    /// and readable only by the account the service runs as.
    /// </summary>
    string StateDirectory { get; }

    /// <summary>
    /// The file an administrator edits to point this machine at an ADL, or
    /// <c>null</c> on a head configured some other way.
    /// </summary>
    /// <remarks>
    /// Here because of what the agent has to say when it has no address: the
    /// useful sentence names the file, and the core must not invent that name
    /// -- <c>agent.ini</c> under <c>%ProgramData%</c> is a Windows Installer
    /// decision (see the Windows head's <c>MachineSettings</c>), and a
    /// systemd head would answer with an environment file or nothing at all.
    /// A path a technician can read out over a telephone is worth more than a
    /// generic instruction, and it is the head that knows it.
    /// </remarks>
    string? SettingsFilePath { get; }
}
