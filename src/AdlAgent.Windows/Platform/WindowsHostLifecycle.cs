using System.Runtime.InteropServices;
using AdlAgent.Core.Platform;

namespace AdlAgent.Windows.Platform;

/// <summary>
/// The host-lifecycle seam on Windows.
/// </summary>
/// <remarks>
/// The service registration itself is one line in <c>Program.cs</c> and needs
/// no abstraction. What the core cannot know is the rest of this: how to
/// describe the machine, when this process started, and above all where a
/// Windows service is allowed to keep state -- <c>%ProgramData%</c>, not
/// beside the executable, which on this platform lives under Program Files
/// and is not writable by the service account.
/// </remarks>
public sealed class WindowsHostLifecycle : IHostLifecycle
{
    /// <summary>The folder under %ProgramData% this agent keeps its state in.</summary>
    public const string StateFolderName = "ADL Agent";

    public WindowsHostLifecycle(TimeProvider time)
    {
        StartedAt = time.GetUtcNow();
        StateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            StateFolderName);
    }

    public string PlatformDescription { get; } = RuntimeInformation.OSDescription;

    public DateTimeOffset StartedAt { get; }

    public string StateDirectory { get; }
}
