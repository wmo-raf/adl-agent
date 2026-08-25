using AdlAgent.Core.Configuration;
using AdlAgent.Core.Control;
using AdlAgent.Core.Cycle;
using AdlAgent.Core.Heartbeat;
using AdlAgent.Core.Hosting;
using AdlAgent.Core.Pairing;
using AdlAgent.Core.Platform;
using AdlAgent.Core.State;
using AdlAgent.Core.Status;
using AdlAgent.Core.Update;
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
/// Only the five platform seams, the clock and the state store are
/// substituted -- everything above them is the shipping code, talking real
/// HTTP.
/// </para>
/// </remarks>
public sealed class AgentHarness : IAsyncDisposable
{
    private bool _started;

    /// <summary>
    /// A fresh agent, or one that has been restarted.
    /// </summary>
    /// <param name="store">
    /// What a previous run left behind. Passing the store of a disposed
    /// harness is how a test says "the service was restarted": the token, the
    /// cached configuration and the sweep log survive, and everything the
    /// agent holds only in memory does not.
    /// </param>
    /// <param name="settings">
    /// Anything else the machine's own settings file would say. The agent
    /// holds almost no local configuration by design (decision #260), so
    /// this is a short list: where ADL is, where state goes, and whether
    /// this machine may replace itself.
    /// </param>
    public AgentHarness(
        InMemoryAgentStateStore? store = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        Store = store ?? new InMemoryAgentStateStore();
        Server = new FakeAdlServer();

        // Its own directory per harness. The only thing the core writes there
        // is a downloaded update package, and the update path deliberately
        // clears that folder before each fetch -- shared between the tests
        // running in parallel, one would delete another's download.
        HostLifecycle.StateDirectory = Directory
            .CreateTempSubdirectory("adl-agent-harness").FullName;
        Time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-21T09:00:00Z"));
        Time.AutoAdvanceAmount = TimeSpan.Zero;

        var configured = new Dictionary<string, string?>
        {
            ["Agent:AdlBaseUrl"] = Server.BaseAddress.ToString().TrimEnd('/'),
        };

        foreach (var setting in settings ?? new Dictionary<string, string?>())
        {
            configured[setting.Key] = setting.Value;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configured)
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
        services.AddSingleton<IUpdateInstaller>(Updates);

        services.AddAdlAgentCore(configuration);

        Services = services.BuildServiceProvider();
    }

    public FakeAdlServer Server { get; }

    public FakeTimeProvider Time { get; }

    public FakeHostLifecycle HostLifecycle { get; } = new();

    public FakeFileMetadataSource Files { get; } = new();

    public FakeFileReadinessProbe Readiness { get; } = new();

    public FakeControlSurface Control { get; } = new();

    public FakeUpdateInstaller Updates { get; } = new();

    public InMemoryAgentStateStore Store { get; }

    public ServiceProvider Services { get; }

    public AgentSession Session => Services.GetRequiredService<AgentSession>();

    public ConfigurationService Configuration => Services.GetRequiredService<ConfigurationService>();

    public AgentCadence Cadence => Services.GetRequiredService<AgentCadence>();

    public HeartbeatMonitor Heartbeats => Services.GetRequiredService<HeartbeatMonitor>();

    public CycleReportStore Cycles => Services.GetRequiredService<CycleReportStore>();

    public AgentStatusReader Status => Services.GetRequiredService<AgentStatusReader>();

    /// <summary>The station list as the tray reads it.</summary>
    public AgentStationsReader Stations => Services.GetRequiredService<AgentStationsReader>();

    /// <summary>One pass of sync, scan, offer and send.</summary>
    public UploadCycle Cycle => Services.GetRequiredService<UploadCycle>();

    /// <summary>The collect a technician asks for at the machine.</summary>
    public OnDemandCollect Collects => Services.GetRequiredService<OnDemandCollect>();

    /// <summary>The configuration re-read a technician asks for at the machine.</summary>
    public OnDemandSync Syncs => Services.GetRequiredService<OnDemandSync>();

    public HeartbeatLoop HeartbeatLoop => Hosted<HeartbeatLoop>();

    public UploadCycleLoop CycleLoop => Hosted<UploadCycleLoop>();

    public AgentControlService ControlService => Hosted<AgentControlService>();

    /// <summary>One check of what ADL says this machine should be running.</summary>
    public UpdateService Updater => Services.GetRequiredService<UpdateService>();

    public UpdateLoop UpdateLoop => Hosted<UpdateLoop>();

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
    /// Wait until every loop is asleep on its timer.
    /// </summary>
    /// <remarks>
    /// The barrier a test needs before it may believe anything a loop was
    /// supposed to have done. Arriving at the fake ADL is not the same
    /// moment as having been acted on: the server records a heartbeat while
    /// it is handling the request, and the agent adopts the cadence that
    /// answer carries some time after the response comes back. A test that
    /// asserted on the cadence the instant the beat landed would be racing
    /// the round trip -- and would pass on a quiet machine and fail on a
    /// loaded CI runner, which is the worst way to find out.
    /// <para>
    /// Loops going quiet is the one observable that means "done": a loop
    /// waiting on the wake signal has finished everything the last wake-up
    /// gave it. There are three of them -- the heartbeat, the scan cycle
    /// and the update check -- and the default waits for all three, because
    /// a test that advanced the clock while one was still working would be
    /// racing it.
    /// </para>
    /// </remarks>
    public async Task AtRestAsync(int loopsAtRest = 3)
    {
        var wake = Services.GetRequiredService<AgentWakeSignal>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (wake.Waiting < loopsAtRest && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5).ConfigureAwait(false);
        }
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
    public async Task AdvanceAsync(TimeSpan by, int loopsAtRest = 3)
    {
        await AtRestAsync(loopsAtRest).ConfigureAwait(false);

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
        Files.Dispose();

        try
        {
            Directory.Delete(HostLifecycle.StateDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private T Hosted<T>() where T : IHostedService =>
        Services.GetServices<IHostedService>().OfType<T>().Single();
}
