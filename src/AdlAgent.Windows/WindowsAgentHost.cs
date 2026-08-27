using AdlAgent.Core;
using AdlAgent.Core.Diagnostics;
using AdlAgent.Core.Hosting;
using AdlAgent.Core.Platform;
using AdlAgent.Windows.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

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
    /// <para>
    /// And it is set through <see cref="LogVerbosity"/> rather than read
    /// straight off the file, because the level can now move while the
    /// program is running: ADL may raise it from the admin, per device
    /// (wmo-raf/adl#307). The pipeline is built once, so what makes that
    /// possible is the change-token source below -- without it a machine HQ
    /// had put on <c>Debug</c> would go on writing <c>Information</c> until
    /// somebody restarted the service, which is the one thing a remote
    /// verbosity must not need.
    /// </para>
    /// </remarks>
    private static void AddFileLog(HostApplicationBuilder builder)
    {
        var options = new AgentOptions();

        builder.Configuration.GetSection(AgentOptions.SectionName).Bind(options);

        var asked = builder.Configuration[$"{AgentOptions.SectionName}:{MachineSettings.LogLevelKey}"];

        var verbosity = new LogVerbosity();

        verbosity.SetLocal(options.LogLevel);

        // Constructed here and registered, rather than resolved: the logging
        // pipeline is configured before the container is built, and the one
        // object both halves read has to exist by then.
        builder.Services.AddSingleton(verbosity);

        // A filter rule and not SetMinimumLevel, which does not work here
        // and looked as though it did. This program ships an appsettings.json
        // carrying a Logging section, and a rule read from configuration
        // beats LoggerFilterOptions.MinLevel outright -- so a machine whose
        // agent.ini said Debug went on writing Information, which is the one
        // failure a verbosity setting must not have. Registered after the
        // configuration's own, so that between two rules of equal reach this
        // is the later and wins.
        builder.Logging.Services.AddSingleton<IConfigureOptions<LoggerFilterOptions>>(
            new VerbosityRule(verbosity, machineAsked: !string.IsNullOrWhiteSpace(asked)));

        builder.Logging.Services.AddSingleton<IOptionsChangeTokenSource<LoggerFilterOptions>>(
            new VerbosityChanges(verbosity));

        // The folder is the head's -- it is the one thing about a log that is
        // platform-shaped -- and everything else about it is the core's.
        var sink = new AgentFileLoggerProvider(
            AgentLogs.In(options.ResolveStateDirectory(new Platform.WindowsHostLifecycle(TimeProvider.System))),
            options.GeneralLogMegabytes,
            // Whatever the filter pipeline lets through. The pipeline is the
            // one authority on verbosity; a second opinion held here would be
            // a second place for the answer to differ.
            LogLevel.Trace,
            TimeProvider.System);

        builder.Logging.AddProvider(sink);

        // The same instance, under the one face the container needs it for:
        // a diagnostics bundle is read off these files and has to know they
        // are up to date first. Registered rather than constructed twice --
        // two providers over one file would be two writers over one file.
        builder.Services.AddSingleton<ILogFlush>(sink);
    }

    /// <summary>The level in force, as a rule the filter pipeline reads.</summary>
    /// <remarks>
    /// Re-run every time the pipeline rebuilds, which is what makes a level
    /// that moves at runtime take effect: the rule is not a value captured at
    /// start-up but a question asked of <see cref="LogVerbosity"/> each time.
    /// </remarks>
    /// <param name="machineAsked">
    /// Whether the machine's settings file named a level at all. When it did
    /// not, and ADL has said nothing either, no rule is added: a developer's
    /// build reads appsettings.json, and a default nobody typed should not
    /// quietly overrule what they put there.
    /// </param>
    private sealed class VerbosityRule(LogVerbosity verbosity, bool machineAsked)
        : IConfigureOptions<LoggerFilterOptions>
    {
        public void Configure(LoggerFilterOptions options)
        {
            if (!machineAsked && !verbosity.Overridden)
            {
                return;
            }

            options.Rules.Add(
                new LoggerFilterRule(
                    providerName: null,
                    categoryName: null,
                    logLevel: verbosity.Effective,
                    filter: null));
        }
    }

    /// <summary>
    /// Tell the logger factory to re-read its rules when the level moves.
    /// </summary>
    /// <remarks>
    /// <c>LoggerFactory</c> watches <c>IOptionsMonitor&lt;LoggerFilterOptions&gt;</c>
    /// and refreshes every logger it has handed out when that fires. This is
    /// the ordinary configuration-reload mechanism, driven by a setting that
    /// arrives over the wire instead of out of a file.
    /// </remarks>
    private sealed class VerbosityChanges : IOptionsChangeTokenSource<LoggerFilterOptions>
    {
        private readonly Lock _gate = new();

        private CancellationTokenSource _moved = new();

        public VerbosityChanges(LogVerbosity verbosity) => verbosity.Changed += Moved;

        public string Name => Options.DefaultName;

        public IChangeToken GetChangeToken()
        {
            lock (_gate)
            {
                return new CancellationChangeToken(_moved.Token);
            }
        }

        private void Moved()
        {
            CancellationTokenSource spent;

            lock (_gate)
            {
                spent = _moved;
                _moved = new CancellationTokenSource();
            }

            // Cancelled outside the lock, because cancelling runs the
            // callbacks -- which ask for the next token -- on this thread.
            spent.Cancel();
            spent.Dispose();
        }
    }
}
