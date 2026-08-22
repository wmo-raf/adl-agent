using System.Security.Authentication;
using AdlAgent.Core.Api;
using AdlAgent.Core.Configuration;
using AdlAgent.Core.Control;
using AdlAgent.Core.Cycle;
using AdlAgent.Core.Heartbeat;
using AdlAgent.Core.Pairing;
using AdlAgent.Core.State;
using AdlAgent.Core.Status;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AdlAgent.Core.Hosting;

/// <summary>
/// Everything the agent is, registered in one call.
/// </summary>
/// <remarks>
/// What is deliberately <em>not</em> here is the four platform seams:
/// <see cref="Platform.IFileMetadataSource"/>,
/// <see cref="Platform.IFileReadinessProbe"/>,
/// <see cref="Platform.IHostLifecycle"/> and
/// <see cref="Platform.IControlSurface"/>. A head registers those in its own
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
        services.TryAddSingleton<UploadCycle>();
        services.TryAddSingleton<AgentCadence>();
        services.TryAddSingleton<AgentWakeSignal>();
        services.TryAddSingleton<HeartbeatMonitor>();
        services.TryAddSingleton<VolumeSpaceReader>();
        services.TryAddSingleton<AgentStatusReader>();
        services.TryAddSingleton<AgentStationsReader>();
        services.TryAddSingleton<StationLinkConfigWriter>();

        // The cycle's report is written by the scan loop and read by the
        // heartbeat, so one instance is registered under both faces.
        services.TryAddSingleton<CycleReportStore>();
        services.TryAddSingleton<ICycleReportSource>(
            static provider => provider.GetRequiredService<CycleReportStore>());

        services.AddHostedService<UploadCycleLoop>();
        services.AddHostedService<HeartbeatLoop>();
        services.AddHostedService<AgentControlService>();

        return services;
    }
}
