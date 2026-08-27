using System.Security.Authentication;
using AdlAgent.Core.Api;
using AdlAgent.Core.Configuration;
using AdlAgent.Core.Control;
using AdlAgent.Core.Cycle;
using AdlAgent.Core.Diagnostics;
using AdlAgent.Core.Heartbeat;
using AdlAgent.Core.Pairing;
using AdlAgent.Core.State;
using AdlAgent.Core.Status;
using AdlAgent.Core.Update;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AdlAgent.Core.Hosting;

/// <summary>
/// Everything the agent is, registered in one call.
/// </summary>
/// <remarks>
/// What is deliberately <em>not</em> here is the five platform seams:
/// <see cref="Platform.IFileMetadataSource"/>,
/// <see cref="Platform.IFileReadinessProbe"/>,
/// <see cref="Platform.IHostLifecycle"/>,
/// <see cref="Platform.IControlSurface"/> and
/// <see cref="Platform.IUpdateInstaller"/>. A head registers those in its own
/// composition root, and that omission is the architecture: the core cannot
/// accidentally acquire a platform default, because it has none to fall back
/// on. A head that forgets one fails to start, loudly, on the machine of
/// whoever is building it.
/// </remarks>
public static class AgentCoreServiceCollectionExtensions
{
    public static IServiceCollection AddAdlAgentCore(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentOptions>()
            .Bind(configuration.GetSection(AgentOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient(AdlApiClient.HttpClientName, static (provider, http) =>
            {
                var options = provider.GetRequiredService<IOptions<AgentOptions>>().Value;

                http.BaseAddress = options.ResolveApiBaseAddress();
                http.Timeout = options.RequestTimeout;
            })
            .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
            {
                // The floor the upload endpoint promises. Stated rather than
                // left to the machine's defaults, because the machines this
                // runs on are old -- Server 2016 is the tested floor and 2012
                // is out there -- and their defaults are older still.
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });

        services.TryAddSingleton<IAdlApiClient, AdlApiClient>();

        services.TryAddSingleton<IAgentStateStore, FileAgentStateStore>();

        services.TryAddSingleton<AgentSession>();
        services.TryAddSingleton<ConfigurationService>();
        services.TryAddSingleton<FileHashCache>();
        services.TryAddSingleton<FolderScanner>();
        services.TryAddSingleton<FolderPreview>();
        services.TryAddSingleton<ReconciliationSweep>();
        services.TryAddSingleton<CycleConcurrency>();
        services.TryAddSingleton<UploadCycle>();
        services.TryAddSingleton<OnDemandSync>();
        services.TryAddSingleton<OnDemandCollect>();
        services.TryAddSingleton<AgentCadence>();
        services.TryAddSingleton<AgentWakeSignal>();
        services.TryAddSingleton<HeartbeatMonitor>();
        services.TryAddSingleton<VolumeSpaceReader>();
        services.TryAddSingleton<AgentStatusReader>();
        services.TryAddSingleton<AgentStationsReader>();
        services.TryAddSingleton<StationLinkConfigWriter>();
        services.TryAddSingleton<UpdateService>();

        // What survives the cycle that wrote it. Registered here rather than
        // beside the logging providers because it is not one: the general
        // sink is built while logging is being configured and owns its own
        // writer, and keeping the two apart is what gives them their two
        // independent ceilings.
        services.TryAddSingleton<CycleLog>();
        services.TryAddSingleton<CycleLogReader>();
        services.TryAddSingleton<DiagnosticsBundle>();

        // Under its flushing face as well, so that a bundle read off these
        // files holds the pass that was still in the queue when somebody
        // pressed the button. The head adds the general sink beside it, and
        // the bundle asks for every registration rather than for either.
        //
        // Added rather than TryAdd'd: this is one of a list, and the same
        // instance under a second face rather than a second instance -- which
        // is why it is a factory over the singleton above and not a type.
        services.AddSingleton<ILogFlush>(
            static provider => provider.GetRequiredService<CycleLog>());

        // The cycle's report is written by the scan loop and read by the
        // heartbeat, so one instance is registered under both faces.
        services.TryAddSingleton<CycleReportStore>();
        services.TryAddSingleton<ICycleReportSource>(
            static provider => provider.GetRequiredService<CycleReportStore>());

        services.AddHostedService<UploadCycleLoop>();
        services.AddHostedService<HeartbeatLoop>();
        services.AddHostedService<UpdateLoop>();
        services.AddHostedService<AgentControlService>();

        return services;
    }
}
