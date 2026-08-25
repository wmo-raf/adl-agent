using AdlAgent.Core.Api;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Configuration;

/// <summary>
/// The re-read of this device's configuration that somebody asks for at the
/// machine, and what came of it.
/// </summary>
/// <remarks>
/// Started and not awaited, for the reason every long thing on this surface
/// is: the control pipe serves one client at a time and its timeout is three
/// seconds, so a command that waited for an HTTP call over the links this
/// product exists for would time out — and, in timing out, report a working
/// service as absent. The command returns at once and the answer arrives on
/// the status the tray already polls every five seconds.
/// <para>
/// Config only. It does not scan and does not upload, which is what makes it
/// a different thing from the collect beside it: pressing Refresh is asking
/// ADL what this machine is meant to be doing, not asking this machine to do
/// it.
/// </para>
/// <para>
/// It is the same <see cref="ConfigurationService.RefreshAsync"/> the cycle
/// calls, so a refresh pressed here and a refresh at the top of a cycle
/// cannot come to different answers, and the offline cache is written by both
/// alike.
/// </para>
/// </remarks>
public sealed class OnDemandSync
{
    private readonly ConfigurationService _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<OnDemandSync> _logger;
    private readonly Lock _gate = new();

    private SyncAttempt? _last;
    private Task? _running;

    public OnDemandSync(
        ConfigurationService configuration, TimeProvider time, ILogger<OnDemandSync> logger)
    {
        _configuration = configuration;
        _time = time;
        _logger = logger;
    }

    /// <summary>The last time somebody asked, and what it came to.</summary>
    public SyncAttempt? Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>
    /// Ask ADL for this device's configuration now.
    /// </summary>
    /// <remarks>
    /// A second press while the first is still travelling is answered by the
    /// first rather than by a second call. On a link slow enough for somebody
    /// to press twice, two calls is exactly what that link cannot spare.
    /// </remarks>
    public SyncAttempt Start()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false } && _last is { } running)
            {
                return running;
            }

            var attempt = new SyncAttempt { StartedAt = _time.GetUtcNow() };

            _last = attempt;
            _running = Task.Run(RefreshAsync, CancellationToken.None);

            return attempt;
        }
    }

    private async Task RefreshAsync()
    {
        var started = Last?.StartedAt ?? _time.GetUtcNow();

        try
        {
            var configuration = await _configuration.RefreshAsync().ConfigureAwait(false);

            // FromCache is the honest distinction, and it is why this is not
            // simply "did it throw". RefreshAsync answers an unreachable ADL
            // with the configuration off the disk rather than with nothing --
            // which is right for the cycle, and would read here as a
            // successful refresh that changed nothing.
            Ended(configuration is { FromCache: false }
                ? new SyncAttempt
                {
                    StartedAt = started,
                    FinishedAt = _time.GetUtcNow(),
                    Ok = true,
                    ConfigVersion = configuration.Version,
                }
                : new SyncAttempt
                {
                    StartedAt = started,
                    FinishedAt = _time.GetUtcNow(),
                    Detail = "ADL is not answering, so this machine is still working "
                        + "from the configuration it last received.",
                });
        }
        catch (Exception exception)
        {
            // Started and not awaited, so nothing above this can catch
            // anything, and an unobserved exception on a service that has to
            // stay up is not a thing to leave lying about.
            _logger.LogWarning(exception, "A requested configuration sync failed.");

            Ended(new SyncAttempt
            {
                StartedAt = started,
                FinishedAt = _time.GetUtcNow(),
                Detail = exception is AdlRequestException refused
                    ? refused.Detail
                    : exception.Message,
            });
        }
    }

    private void Ended(SyncAttempt attempt)
    {
        lock (_gate)
        {
            _last = attempt;
        }
    }
}

/// <summary>
/// One requested sync: when it was asked for, and what it came to.
/// </summary>
/// <remarks>
/// Both moments are carried because the window watching has to tell its own
/// press from somebody else's, and from the one before it. A single "last
/// synced" cannot: it moves on every cycle, so a technician pressing Refresh
/// on an unreachable ADL would watch it move a minute later and read that as
/// their press having worked.
/// </remarks>
public sealed record SyncAttempt
{
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When it finished, or null while it is still going.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>True when ADL actually answered.</summary>
    public bool Ok { get; init; }

    /// <summary>The configuration version ADL served, when it did.</summary>
    public long? ConfigVersion { get; init; }

    /// <summary>Why it did not, when it did not.</summary>
    public string? Detail { get; init; }
}
