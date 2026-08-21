using AdlAgent.Core.Api;
using AdlAgent.Core.Pairing;
using AdlAgent.Core.State;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Configuration;

/// <summary>
/// Fetch the device's configuration from ADL, and keep working when ADL is
/// not there.
/// </summary>
/// <remarks>
/// Both halves of story 11 live here and are deliberately not separable: a
/// fetch that succeeds writes the cache, and a fetch that cannot happen
/// reads it. A machine on a link that is down for a day keeps scanning the
/// folders it was last told about, and the day the link returns it picks up
/// whatever HQ changed in the meantime -- neither behaviour needing anyone
/// in-country to do anything.
/// <para>
/// What this does <em>not</em> do is decide the cache is too old to use.
/// There is no age at which "the last thing HQ told me" becomes worse than
/// nothing, and a staleness cutoff would turn a long outage into data loss
/// on top of an outage.
/// </para>
/// </remarks>
public sealed class ConfigurationService
{
    private readonly IAdlApiClient _client;
    private readonly IAgentStateStore _store;
    private readonly AgentSession _session;
    private readonly TimeProvider _time;
    private readonly ILogger<ConfigurationService> _logger;
    private readonly Lock _gate = new();

    private AgentConfiguration? _current;
    private DateTimeOffset? _lastSyncedAt;
    private bool _loadedFromDisk;

    public ConfigurationService(
        IAdlApiClient client,
        IAgentStateStore store,
        AgentSession session,
        TimeProvider time,
        ILogger<ConfigurationService> logger)
    {
        _client = client;
        _store = store;
        _session = session;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The configuration in force, without going to the network. Null only
    /// on a machine that has never completed a sync.
    /// </summary>
    public AgentConfiguration? Current
    {
        get
        {
            EnsureCacheLoaded();

            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>When ADL was last actually reached. Not when the cache was read.</summary>
    public DateTimeOffset? LastSyncedAt
    {
        get
        {
            lock (_gate)
            {
                return _lastSyncedAt;
            }
        }
    }

    /// <summary>
    /// Ask ADL for this device's world; fall back to the cache if it cannot
    /// be asked.
    /// </summary>
    /// <returns>
    /// The configuration to work from, or <c>null</c> when this machine has
    /// never synced and ADL is unreachable -- the one case where there is
    /// genuinely nothing to do.
    /// </returns>
    public async Task<AgentConfiguration?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        EnsureCacheLoaded();

        var token = _session.ActiveToken;

        if (token is null)
        {
            // Not paired, or paired and refused. Either way this is not a
            // failure to sync -- there is nobody to sync with yet.
            return Current;
        }

        try
        {
            var sync = await _client.SyncAsync(token, cancellationToken).ConfigureAwait(false);
            var fetchedAt = _time.GetUtcNow();

            _store.SaveConfig(sync, fetchedAt);

            var configuration = new AgentConfiguration
            {
                Sync = sync,
                FetchedAt = fetchedAt,
                FromCache = false,
            };

            lock (_gate)
            {
                _current = configuration;
                _lastSyncedAt = fetchedAt;
            }

            _logger.LogDebug(
                "Synced configuration version {Version}: {Connections} connection(s), {Links} station link(s).",
                sync.ConfigVersion,
                sync.Connections.Count,
                configuration.StationLinks.Count());

            return configuration;
        }
        catch (DeviceRevokedException)
        {
            _session.MarkRevoked();

            return Current;
        }
        catch (AdlUnreachableException exception)
        {
            var cached = Current;

            if (cached is null)
            {
                _logger.LogWarning(
                    exception,
                    "ADL is unreachable and this machine has never synced, so there is nothing to work from yet.");

                return null;
            }

            _logger.LogWarning(
                "ADL is unreachable; working from the configuration cached at {FetchedAt:u}.",
                cached.FetchedAt);

            return MarkStale(cached);
        }
    }

    /// <summary>
    /// Read the cache once, on first use rather than in the constructor:
    /// a service being constructed should not be able to fail on a disk.
    /// </summary>
    private void EnsureCacheLoaded()
    {
        lock (_gate)
        {
            if (_loadedFromDisk)
            {
                return;
            }

            _loadedFromDisk = true;

            var cached = _store.LoadConfig();

            if (cached is null)
            {
                return;
            }

            _current = new AgentConfiguration
            {
                Sync = cached.Config,
                FetchedAt = cached.FetchedAt,
                FromCache = true,
            };
        }
    }

    private AgentConfiguration MarkStale(AgentConfiguration configuration)
    {
        if (configuration.FromCache)
        {
            return configuration;
        }

        var stale = configuration with { FromCache = true };

        lock (_gate)
        {
            _current = stale;
        }

        return stale;
    }
}
