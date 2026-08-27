using AdlAgent.Core;
using AdlAgent.Core.Diagnostics;
using AdlAgent.Core.Hosting;
using AdlAgent.Core.Platform;
using AdlAgent.Windows.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        AddFileLog(builder);

        builder.Services.AddSingleton<IHostLifecycle, WindowsHostLifecycle>();
        builder.Services.AddSingleton<IFileMetadataSource, WindowsFileMetadataSource>();
        builder.Services.AddSingleton<IFileReadinessProbe, WindowsFileReadinessProbe>();
        builder.Services.AddSingleton<IControlSurface, NamedPipeControlSurface>();
        builder.Services.AddSingleton<IUpdateInstaller, WindowsUpdateInstaller>();

        return builder;
    }

    /// <summary>
    /// Send <see cref="ILogger"/> output to a file under the state directory.
    /// </summary>
    /// <remarks>
    /// Before this, the head registered no logging provider at all. On the
    /// service tier that meant <c>ILogger</c> output went to the Windows Event
    /// Log -- findable, unstructured, and interleaved with everything else on
    /// the machine -- and on the per-user tray tier it went to a console
    /// window that closes, which is to say nowhere. That tier had no durable
    /// record of a crash, a TLS failure or an unhandled exception, and this
    /// is its first.
    /// <para>
    /// Registered on both tiers by one call, because they are one program
    /// started two ways, and the tier with no administrator is the one that
    /// needed this most.
    /// </para>
    /// <para>
    /// The minimum level is set on the whole logging builder rather than only
    /// on this provider: a machine an administrator has put on <c>Debug</c>
    /// for a day has said something about what the machine should record, not
    /// about which sink should record it.
    /// </para>
    /// </remarks>
    private static void AddFileLog(HostApplicationBuilder builder)
    {
        var options = new AgentOptions();

        builder.Configuration.GetSection(AgentOptions.SectionName).Bind(options);

        var level = Enum.TryParse<LogLevel>(options.LogLevel, ignoreCase: true, out var parsed)
            ? parsed
            // A level nobody can parse is a typo in a file somebody edited
            // over a telephone, and the answer to a typo is the default
            // rather than silence.
            : LogLevel.Information;

        builder.Logging.SetMinimumLevel(level);

        // The folder is the head's -- it is the one thing about a log that is
        // platform-shaped -- and everything else about it is the core's.
        var sink = new AgentFileLoggerProvider(
            AgentLogs.In(
                string.IsNullOrWhiteSpace(options.StateDirectory)
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        Platform.WindowsHostLifecycle.StateFolderName)
                    : options.StateDirectory),
            options.GeneralLogMegabytes,
            level,
            TimeProvider.System);

        builder.Logging.AddProvider(sink);

        // The same instance, under the one face the container needs it for:
        // a diagnostics bundle is read off these files and has to know they
        // are up to date first. Registered rather than constructed twice --
        // two providers over one file would be two writers over one file.
        builder.Services.AddSingleton<ILogFlush>(sink);
    }
}
