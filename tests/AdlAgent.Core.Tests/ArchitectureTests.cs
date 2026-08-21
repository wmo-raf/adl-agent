using System.Text.RegularExpressions;
using AdlAgent.Core.Configuration;
using AdlAgent.Core.Control;
using AdlAgent.Core.Heartbeat;
using AdlAgent.Core.Pairing;
using AdlAgent.Core.Platform;
using AdlAgent.Core.State;
using AdlAgent.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The two rules the app structure rests on, made into tests.
/// </summary>
/// <remarks>
/// The spec states them as discipline: no platform conditionals in the core,
/// ever, and the platform providers injected at the composition root. A rule
/// that only lives in a document is a rule the fifth contributor breaks by
/// accident, so both are checked here.
/// </remarks>
public class ArchitectureTests
{
    [Fact]
    public void The_core_contains_no_platform_conditional()
    {
        var offenders = new List<string>();

        // What a platform check looks like in C#. A hit is not necessarily
        // wrong -- but it is always a design decision, and the decision the
        // spec made is that it belongs in a head.
        var checks = new Regex(
            @"OperatingSystem\.Is|RuntimeInformation\.IsOSPlatform|#if\s+WINDOWS|#if\s+LINUX|SupportedOSPlatform",
            RegexOptions.Compiled);

        foreach (var file in Directory.EnumerateFiles(CoreSourceDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var match = checks.Match(File.ReadAllText(file));

            if (match.Success)
            {
                offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_Windows_head_supplies_every_seam_the_core_needs()
    {
        using var host = BuildWindowsHost();

        // Resolved, not merely registered: a seam whose implementation cannot
        // be constructed is a service that fails at start-up on a machine in
        // the field.
        Assert.IsType<Windows.Platform.WindowsHostLifecycle>(
            host.Services.GetRequiredService<IHostLifecycle>());
        Assert.IsType<Windows.Platform.WindowsFileMetadataSource>(
            host.Services.GetRequiredService<IFileMetadataSource>());
        Assert.IsType<Windows.Platform.WindowsFileReadinessProbe>(
            host.Services.GetRequiredService<IFileReadinessProbe>());
        Assert.IsType<Windows.Platform.NamedPipeControlSurface>(
            host.Services.GetRequiredService<IControlSurface>());
    }

    [Fact]
    public void The_Windows_head_runs_the_three_loops_the_agent_is_made_of()
    {
        using var host = BuildWindowsHost();

        var loops = host.Services.GetServices<IHostedService>().ToList();

        Assert.Contains(loops, loop => loop is HeartbeatLoop);
        Assert.Contains(loops, loop => loop is ConfigurationSyncLoop);
        Assert.Contains(loops, loop => loop is AgentControlService);

        // The session and the state store come up too, which is the whole of
        // what a paired machine needs to remember it is paired.
        Assert.NotNull(host.Services.GetRequiredService<AgentSession>());
        Assert.IsType<FileAgentStateStore>(host.Services.GetRequiredService<IAgentStateStore>());
    }

    private static IHost BuildWindowsHost()
    {
        var state = Directory.CreateTempSubdirectory("adl-agent-composition").FullName;

        var builder = WindowsAgentHost.CreateBuilder([
            "--Agent:AdlBaseUrl=https://adl.example.org",
            $"--Agent:StateDirectory={state}",
        ]);

        return builder.Build();
    }

    /// <summary>
    /// The core's sources, found by walking up from the test binary to the
    /// repository root.
    /// </summary>
    private static string CoreSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var core = Path.Combine(directory.FullName, "src", "AdlAgent.Core");

            if (Directory.Exists(core))
            {
                return core;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find src/AdlAgent.Core above the test binary. This test reads the core's sources.");
    }
}
