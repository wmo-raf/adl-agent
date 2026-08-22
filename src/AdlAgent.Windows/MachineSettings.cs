namespace AdlAgent.Windows;

/// <summary>
/// The settings file an installer writes, and an administrator can edit.
/// </summary>
/// <remarks>
/// Where a machine's ADL is cannot come from ADL, so it has to be put on the
/// machine by whatever installed the agent. An MSI can write an INI file
/// directly, which is why this is one: there is no way to have Windows
/// Installer fill in a value inside a JSON document, and every alternative
/// route was worse.
/// <para>
/// A machine-wide environment variable was the obvious one and is a trap:
/// the Service Control Manager takes its environment block at boot and does
/// not re-read it, so a service installed and started in one Windows
/// Installer transaction starts without the variable that transaction just
/// set -- and then fails, restarts, and fails again until somebody reboots
/// the server. A file is read when the service starts, by the service.
/// </para>
/// <para>
/// It sits in the state directory rather than beside the program, so an
/// upgrade -- which replaces everything under Program Files -- cannot lose
/// it, and so an administrator diagnosing a machine finds the configuration
/// in the same place as the token and the logs.
/// </para>
/// </remarks>
public static class MachineSettings
{
    /// <summary>What the file is called, under the state directory.</summary>
    public const string FileName = "agent.ini";

    /// <summary>
    /// The section an INI file's keys are read under, which is the same
    /// section the agent's own options bind to: <c>[Agent] AdlBaseUrl=…</c>
    /// arrives as <c>Agent:AdlBaseUrl</c>.
    /// </summary>
    public const string Section = "Agent";

    /// <summary>Where the file lives for a machine whose state is in <paramref name="stateDirectory"/>.</summary>
    public static string PathIn(string stateDirectory) =>
        Path.Combine(stateDirectory, FileName);
}
