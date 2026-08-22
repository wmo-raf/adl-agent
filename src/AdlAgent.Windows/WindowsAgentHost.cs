using AdlAgent.Core.Hosting;
using AdlAgent.Core.Platform;
using AdlAgent.Windows.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdlAgent.Windows;

/// <summary>
/// The composition root the whole architecture rests on.
/// </summary>
/// <remarks>
/// Everything the agent does lives in <c>AdlAgent.Core</c>, which contains no
/// platform conditional anywhere. The five registrations below are the entire
/// Windows-specific surface of this program: replace them with systemd and
/// unix-socket equivalents and the same core runs on Linux, which is what
/// "designed-for-later" has to mean if it is to mean anything.
/// <para>
/// A method rather than lines in <c>Program.cs</c> so that a test can build
/// this exact graph and prove it resolves. A head that forgot a seam is a
/// machine that fails to start in a country nobody can reach; it should fail
/// on the machine of whoever wrote it instead.
/// </para>
/// </remarks>
public static class WindowsAgentHost
{
    /// <summary>The name Windows knows the service by.</summary>
    public const string ServiceName = "ADL Agent";

    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Seam 3, the framework half: run as a Windows Service when Windows
        // started us as one, and as a plain console process otherwise --
        // which is how this is debugged, and how it runs under the per-user
        // tier for technicians without administrator rights.
        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
        }

        // Where this machine's ADL is, as the installer wrote it.
        //
        // Inserted here and not later because of what comes after it: the
        // environment variables and the command line are re-added below so
        // that they still win, which is what lets a developer point a build
        // at a local instance on a machine that has an installed agent on it.
        // The file itself is optional -- a machine configured by environment
        // variable alone has none -- and is read at start-up, by this
        // process, which is the whole reason it is a file. See MachineSettings.
        builder.Configuration.AddIniFile(
            MachineSettings.PathIn(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    Platform.WindowsHostLifecycle.StateFolderName)),
            optional: true,
            reloadOnChange: false);

        builder.Configuration.AddEnvironmentVariables();

        if (args.Length > 0)
        {
            builder.Configuration.AddCommandLine(args);
        }

        builder.Services.AddAdlAgentCore(builder.Configuration);

        builder.Services.AddSingleton<IHostLifecycle, WindowsHostLifecycle>();
        builder.Services.AddSingleton<IFileMetadataSource, WindowsFileMetadataSource>();
        builder.Services.AddSingleton<IFileReadinessProbe, WindowsFileReadinessProbe>();
        builder.Services.AddSingleton<IControlSurface, NamedPipeControlSurface>();
        builder.Services.AddSingleton<IUpdateInstaller, WindowsUpdateInstaller>();

        return builder;
    }
}
