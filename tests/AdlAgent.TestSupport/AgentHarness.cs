using AdlAgent.Core.Configuration;
using AdlAgent.Core.Control;
using AdlAgent.Core.Heartbeat;
using AdlAgent.Core.Hosting;
using AdlAgent.Core.Pairing;
using AdlAgent.Core.Platform;
using AdlAgent.Core.State;
using AdlAgent.Core.Status;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AdlAgent.TestSupport;

/// <summary>
/// A whole agent, pointed at a fake ADL, with the platform faked and the
/// clock in the test's hand.
/// </summary>
/// <remarks>
/// Assembled through the same <c>AddAdlAgentCore</c> call the Windows head
/// makes, so the wiring is under test alongside the behaviour: a service the
/// composition root forgot fails here, in a test, rather than on a server in
/// a country nobody can reach.
/// <para>
/// Only the four platform seams, the clock and the state store are
/// substituted -- everything above them is the shipping code, talking real
/// HTTP.
/// </para>
/// </remarks>
public sealed class AgentHarness : IAsyncDisposable
{
    private bool _started;

    public AgentHarness()
    {
        Server = new FakeAdlServer();
        Time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-21T09:00:00Z"));
        Time.AutoAdvanceAmount = TimeSpan.Zero;

        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:AdlBaseUrl"] = Server.BaseAddress.ToString().TrimEnd('/'),
            })
            .Build();

        var services = new ServiceCollection();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug));

        // Registered before the core, which uses TryAdd throughout: a test's
        // substitution wins, and a seam nobody substituted still has to come
        // from somewhere.
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IAgentStateStore>(Store);
        services.AddSingleton<IHostLifecycle>(HostLifecycle);
        services.AddSingleton<IFileMetadataSource>(Files);
        services.AddSingleton<IFileReadinessProbe>(Readiness);
        services.AddSingleton<IControlSurface>(Control);

        services.AddAdlAgentCore(settings);

        Services = services.BuildServiceProvider();
    }

    public FakeAdlServer Server { get; }

    public FakeTimeProvider Time { get; }

    public FakeHostLifecycle HostLifecycle { get; } = new();

    public FakeFileMetadataSource Files { get; } = new();

    public FakeFileReadinessProbe Readiness { get; } = new();

    public FakeControlSurface Control { get; } = new();

    public InMemoryAgentStateStore Store { get; } = new();

    public ServiceProvider Services { get; }

    public AgentSession Session => Services.GetRequiredService<AgentSession>();

    public ConfigurationService Configuration => Services.GetRequiredService<ConfigurationService>();

    public AgentCadence Cadence => Services.GetRequiredService<AgentCadence>();

    public HeartbeatMonitor Heartbeats => Services.GetRequiredService<HeartbeatMonitor>();

    public CycleReportStore Cycles => Services.GetRequiredService<CycleReportStore>();

    public AgentStatusReader Status => Services.GetRequiredService<AgentStatusReader>();

    public HeartbeatLoop HeartbeatLoop => Hosted<HeartbeatLoop>();

    public ConfigurationSyncLoop SyncLoop => Hosted<ConfigurationSyncLoop>();

    public AgentControlService ControlService => Hosted<AgentControlService>();

    /// <summary>Start every loop, exactly as the host would.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (var service in Services.GetServices<IHostedService>())
        {
            await service.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        _started = true;
    }

    /// <summary>
    /// Move the agent's clock on, once every loop is asleep on its timer.
    /// </summary>
    /// <remarks>
    /// The wait is the whole point. A loop still between "beat sent" and
    /// "start waiting" has no timer for the fake clock to fire, so advancing
    /// before it gets there would leave it waiting the full interval from the
    /// new now -- and the test would fail, or worse pass, depending on how
    /// busy the machine was. Waiting on the signal's own count makes the
    /// cadence assertions mean what they say.
    /// </remarks>
    public async Task AdvanceAsync(TimeSpan by, int loopsAtRest = 2)
    {
        var wake = Services.GetRequiredService<AgentWakeSignal>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (wake.Waiting < loopsAtRest && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5).ConfigureAwait(false);
        }

        Time.Advance(by);
    }

    /// <summary>Pair this agent, the way the tray does.</summary>
    public async Task<ControlResponse> PairAsync(string pairingCode = "TEST-CODE")
    {
        Server.AddPairingCode(pairingCode);

        return await ControlService.HandleAsync(new ControlRequest(
            ControlProtocol.PairCommand,
            new System.Text.Json.Nodes.JsonObject { ["pairing_code"] = pairingCode }))
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            foreach (var service in Services.GetServices<IHostedService>())
            {
                try
                {
                    await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        await Services.DisposeAsync().ConfigureAwait(false);

        Server.Dispose();
    }

    private T Hosted<T>() where T : IHostedService =>
        Services.GetServices<IHostedService>().OfType<T>().Single();
}
