using AdlAgent.Core.Api;
using AdlAgent.Core.Configuration;
using AdlAgent.Core.Hosting;
using AdlAgent.Core.Pairing;
using AdlAgent.Core.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdlAgent.Core.Heartbeat;

/// <summary>
/// Say "I am here", every few minutes, whatever else is happening.
/// </summary>
/// <remarks>
/// The isolation is the feature. This loop shares no lock, no queue and no
/// failure with the scan cycle; it reads the cycle's last report and never
/// waits on it. That is what buys HQ the distinction they have never had with
/// reverse tunnels -- "the machine is down" versus "the machine is up and its
/// work has wedged" -- and it only holds as long as nothing in this file
/// starts depending on the cycle making progress.
/// <para>
/// Nothing here throws. A beat that cannot be sent is recorded and the loop
/// sleeps; a machine whose heartbeat loop died would look exactly like a
/// machine that is off, which is the one lie this agent must never tell.
/// </para>
/// </remarks>
public sealed class HeartbeatLoop : AgentLoop
{
    /// <summary>
    /// The skew worth saying something about locally. ADL raises its own
    /// advisory at the same distance; this is the half the person standing at
    /// the machine can act on.
    /// </summary>
    private const int WorryingClockSkewSeconds = 300;

    private readonly IAdlApiClient _client;
    private readonly AgentSession _session;
    private readonly ConfigurationService _configuration;
    private readonly ICycleReportSource _cycles;
    private readonly VolumeSpaceReader _volumes;
    private readonly HeartbeatMonitor _monitor;
    private readonly AgentCadence _cadence;
    private readonly IHostLifecycle _host;
    private readonly TimeProvider _time;
    private readonly ILogger<HeartbeatLoop> _logger;

    public HeartbeatLoop(
        IAdlApiClient client,
        AgentSession session,
        ConfigurationService configuration,
        ICycleReportSource cycles,
        VolumeSpaceReader volumes,
        HeartbeatMonitor monitor,
        AgentCadence cadence,
        AgentWakeSignal wake,
        IHostLifecycle host,
        TimeProvider time,
        IOptions<AgentOptions> options,
        ILogger<HeartbeatLoop> logger)
        : base(wake, time, options, logger)
    {
        _client = client;
        _session = session;
        _configuration = configuration;
        _cycles = cycles;
        _volumes = volumes;
        _monitor = monitor;
        _cadence = cadence;
        _host = host;
        _time = time;
        _logger = logger;
    }

    protected override TimeSpan Interval => _cadence.HeartbeatInterval;

    protected override Task RunOnceAsync(CancellationToken cancellationToken) =>
        BeatAsync(cancellationToken);

    /// <summary>
    /// Send one beat. Never throws; a failure is a fact about this machine's
    /// link, not a reason to stop being able to report.
    /// </summary>
    public async Task BeatAsync(CancellationToken cancellationToken = default)
    {
        var token = _session.ActiveToken;

        if (token is null)
        {
            // An unpaired machine has nobody to report to, and a revoked one
            // has been told to stop. Neither is a missed beat.
            return;
        }

        var sentAt = _time.GetUtcNow();

        // Read before the send and settled after it. What is on the queue at
        // this instant is what this beat is answerable for; a pass that
        // finishes while the request is in flight belongs to the next one.
        var batch = _cycles.Peek();

        try
        {
            var response = await _client
                .HeartbeatAsync(token, Compose(sentAt, batch), cancellationToken)
                .ConfigureAwait(false);

            // Only now. Everything below this line is a beat that did not
            // arrive, and its passes are still owed -- which is the whole of
            // what makes a refused beat cost nothing.
            _cycles.Delivered(batch);

            _monitor.RecordSuccess(response, sentAt);
            _cadence.Adopt(
                heartbeatMinutes: response.HeartbeatIntervalMinutes,
                checkMinutes: response.CheckIntervalMinutes);

            if (response.ClockSkewSeconds is { } skew && Math.Abs(skew) >= WorryingClockSkewSeconds)
            {
                _logger.LogWarning(
                    "This machine's clock is {Skew} seconds away from ADL's. File windows are measured against it, so fix the clock.",
                    skew);
            }
        }
        catch (DeviceRevokedException exception)
        {
            _session.MarkRevoked();
            _monitor.RecordFailure(exception.Detail, sentAt);
        }
        catch (AdlUnreachableException exception)
        {
            _monitor.RecordFailure(exception.Message, sentAt);

            _logger.LogWarning("Heartbeat did not reach ADL: {Reason}", exception.Message);
        }
        catch (AdlRequestException exception)
        {
            _monitor.RecordFailure(exception.Detail, sentAt);

            _logger.LogWarning(
                "ADL refused the heartbeat ({Code}): {Detail}", exception.Code, exception.Detail);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _monitor.RecordFailure(exception.Message, sentAt);

            _logger.LogError(exception, "Could not send a heartbeat.");
        }
    }

    /// <summary>
    /// Everything the machine can say about itself right now.
    /// </summary>
    /// <remarks>
    /// Assembled from whatever is available and never from a call that could
    /// fail: the disk read swallows its own errors, the cycle report is
    /// whatever the cycle last left, and the folder list comes from the
    /// cached configuration. A beat gets sent even on a machine where
    /// everything else has gone wrong, because that is the machine whose
    /// beat matters most.
    /// </remarks>
    private HeartbeatRequest Compose(DateTimeOffset now, PassBatch batch)
    {
        var configuration = _configuration.Current;

        var folders = configuration is null
            ? []
            : configuration.StationLinks
                .Select(link => link.Config.LocalFolderPath)
                .Where(path => !string.IsNullOrWhiteSpace(path));

        return new HeartbeatRequest
        {
            AppVersion = AgentVersion.Current,
            OsVersion = _host.PlatformDescription,
            UptimeSeconds = (long)Math.Max(0, (now - _host.StartedAt).TotalSeconds),
            DeviceTime = now,
            BacklogCount = _cycles.BacklogCount,
            LastCycle = _cycles.LastCompletedCycle,
            CompletedPasses = batch.Passes,
            // Left out entirely on the ordinary beat, which is nearly all of
            // them: a zero every five minutes is a field ADL learns to read
            // past, and this one is meant to be noticed.
            DroppedPasses = batch.Dropped > 0 ? batch.Dropped : null,
            Disk = _volumes.Read(folders),
        };
    }
}
