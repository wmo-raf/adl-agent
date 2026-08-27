using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using AdlAgent.Core.Cycle;
using AdlAgent.Core.Control;
using AdlAgent.Core.Heartbeat;
using AdlAgent.Core.Pairing;
using AdlAgent.Core.Platform;
using AdlAgent.Core.State;
using AdlAgent.Core.Update;
using AdlAgent.Tray;
using AdlAgent.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The rules the app structure rests on, made into tests.
/// </summary>
/// <remarks>
/// The spec states the first two as discipline: no platform conditionals in
/// the core, ever, and the platform providers injected at the composition
/// root. A rule that only lives in a document is a rule the fifth contributor
/// breaks by accident, so they are checked here.
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
        Assert.IsType<Windows.Platform.WindowsUpdateInstaller>(
            host.Services.GetRequiredService<IUpdateInstaller>());
    }

    [Fact]
    public void The_Windows_head_runs_the_four_loops_the_agent_is_made_of()
    {
        using var host = BuildWindowsHost();

        var loops = host.Services.GetServices<IHostedService>().ToList();

        Assert.Contains(loops, loop => loop is HeartbeatLoop);
        Assert.Contains(loops, loop => loop is UploadCycleLoop);
        Assert.Contains(loops, loop => loop is AgentControlService);
        Assert.Contains(loops, loop => loop is UpdateLoop);

        // The session and the state store come up too, which is the whole of
        // what a paired machine needs to remember it is paired.
        Assert.NotNull(host.Services.GetRequiredService<AgentSession>());
        Assert.IsType<FileAgentStateStore>(host.Services.GetRequiredService<IAgentStateStore>());
    }

    /// <summary>
    /// The window's view models stay somewhere a test can reach them.
    /// </summary>
    /// <remarks>
    /// They began inside the WPF assembly, which is <c>net10.0-windows</c>,
    /// and a <c>net10.0</c> test project cannot reference one of those --
    /// so the decisions a technician actually reads off the screen were
    /// covered by nothing but somebody reading them. Moving them was the
    /// point of wmo-raf/adl#297, and the way that quietly comes undone is
    /// somebody adding the next view model to the project the window is in,
    /// where it compiles perfectly and can never be tested.
    /// </remarks>
    [Fact]
    public void The_windows_view_models_are_in_an_assembly_the_tests_can_reference()
    {
        var framework = typeof(ShellViewModel).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

        Assert.NotNull(framework);
        Assert.DoesNotContain("windows", framework, StringComparison.OrdinalIgnoreCase);

        // And the same assembly, so a second one does not appear beside it
        // with the next decision in it.
        Assert.Equal(typeof(ShellViewModel).Assembly, typeof(NextStep).Assembly);
        Assert.Equal(typeof(ShellViewModel).Assembly, typeof(StationViewModel).Assembly);
    }

    /// <summary>
    /// The supply chain stays five Microsoft packages and Velopack.
    /// </summary>
    /// <remarks>
    /// A rule about a binary that is installed inside 26 government networks,
    /// several of which run a review before anything reaches a server. Every
    /// package added here is a thing somebody has to answer for, and the way
    /// one arrives is never a decision -- it is a convenience halfway through
    /// an unrelated change.
    /// <para>
    /// The rolling, gzipping and evicting log this repository has is the
    /// worked example: Serilog and <c>NReco.Logging.File</c> would each have
    /// done the job, and each would also have brought a second, independent
    /// retention model that knows nothing about the cycle log's ceiling.
    /// </para>
    /// <para>
    /// A test rather than a note, because a note is what the fifth
    /// contributor does not read. Adding a package is still allowed; what is
    /// not allowed is adding one quietly.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_agent_depends_on_nothing_new()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // The shipping programs.
            "Microsoft.Extensions.Hosting.Abstractions",
            "Microsoft.Extensions.Http",
            "Microsoft.Extensions.Logging.Abstractions",
            "Microsoft.Extensions.Options.ConfigurationExtensions",
            "Microsoft.Extensions.Options.DataAnnotations",
            "Microsoft.Extensions.Configuration.Ini",
            "Microsoft.Extensions.Hosting",
            "Microsoft.Extensions.Hosting.WindowsServices",
            "Velopack",

            // And the test projects, which ship to nobody.
            "Microsoft.Extensions.Logging.Console",
            "Microsoft.Extensions.TimeProvider.Testing",
            "Microsoft.NET.Test.Sdk",
            "xunit",
            "xunit.runner.visualstudio",
        };

        var references = new Regex(
            @"<PackageReference\s+Include=""(?<name>[^""]+)""", RegexOptions.Compiled);

        var offenders = new List<string>();

        foreach (var project in Directory.EnumerateFiles(
            RepositoryDirectory(), "*.csproj", SearchOption.AllDirectories))
        {
            foreach (Match reference in references.Matches(File.ReadAllText(project)))
            {
                var name = reference.Groups["name"].Value;

                if (!allowed.Contains(name))
                {
                    offenders.Add($"{Path.GetFileName(project)}: {name}");
                }
            }
        }

        Assert.Empty(offenders);
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
    private static string CoreSourceDirectory() =>
        Path.Combine(RepositoryDirectory(), "src", "AdlAgent.Core");

    /// <summary>
    /// The repository, found by walking up from the test binary.
    /// </summary>
    private static string RepositoryDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "AdlAgent.Core")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find src/AdlAgent.Core above the test binary. These tests read the repository.");
    }
}
