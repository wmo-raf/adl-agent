using System.Text.Json.Nodes;
using AdlAgent.Core.Api;
using AdlAgent.Core.Hosting;
using AdlAgent.Core.Pairing;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Configuration;

/// <summary>
/// The app tier, written from the machine and stored in ADL.
/// </summary>
/// <remarks>
/// The direction is the decision (#260). ADL holds all durable configuration
/// and this agent is an editor of it, not a second copy: a folder binding
/// typed at the machine goes to ADL and comes back on the next sync, so an
/// administrator on another continent can see and correct what the person
/// in-country set up (story 8). Nothing is applied locally on the way past.
/// <para>
/// That is why a write against an unreachable ADL is a refusal rather than
/// something queued. A queued write would be a setting that exists on the
/// machine and not in ADL for as long as the link is down -- which is
/// precisely the split this design exists to remove -- and the technician
/// standing there can simply press the button again.
/// </para>
/// </remarks>
public sealed class StationLinkConfigWriter
{
    private readonly IAdlApiClient _client;
    private readonly ConfigurationService _configuration;
    private readonly AgentSession _session;
    private readonly AgentWakeSignal _wake;
    private readonly ILogger<StationLinkConfigWriter> _logger;

    public StationLinkConfigWriter(
        IAdlApiClient client,
        ConfigurationService configuration,
        AgentSession session,
        AgentWakeSignal wake,
        ILogger<StationLinkConfigWriter> logger)
    {
        _client = client;
        _configuration = configuration;
        _session = session;
        _wake = wake;
        _logger = logger;
    }

    /// <summary>
    /// Change one station link's settings in ADL, then work from what ADL
    /// now holds.
    /// </summary>
    /// <exception cref="NotPairedException">This machine has no token to write with.</exception>
    /// <exception cref="UnknownStationLinkException">
    /// This device's configuration has no such station link.
    /// </exception>
    /// <exception cref="DeviceRevokedException">The token is no longer good.</exception>
    /// <exception cref="AdlRequestException">ADL would not write those settings.</exception>
    /// <exception cref="AdlUnreachableException">ADL could not be reached.</exception>
    public async Task<ConfigWriteResponse> WriteAsync(
        long stationLinkId, JsonObject changes, CancellationToken cancellationToken = default)
    {
        var token = _session.ActiveToken
            ?? throw new NotPairedException();

        // Checked here rather than left to ADL's 404, because the answer is
        // more useful from this side: the tray is drawing a list this machine
        // was given, so a station id that is not in it means the list is
        // stale, and there is nothing ADL could add to that.
        var known = _configuration.Current?.StationLinks
            .Any(link => link.Id == stationLinkId) ?? false;

        if (!known)
        {
            throw new UnknownStationLinkException(stationLinkId);
        }

        ConfigWriteResponse written;

        try
        {
            written = await _client
                .UpdateStationLinkConfigAsync(token, stationLinkId, changes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DeviceRevokedException)
        {
            // The same conclusion the sync and the cycle draw from a 401, and
            // it has to be drawn here too: a technician who has just been
            // refused should be shown "re-pair this machine" by the same tray
            // that refused them, not on the next cycle.
            _session.MarkRevoked();

            throw;
        }

        _logger.LogInformation(
            "Station link {StationLink} was reconfigured from this machine; ADL is now at version {Version}.",
            stationLinkId,
            written.ConfigVersion);

        // Read back rather than patched in. ADL may have normalised what was
        // sent, and a shared-tier write elsewhere may have landed in between;
        // what the machine works from should be what ADL holds, always.
        await _configuration.RefreshAsync(cancellationToken).ConfigureAwait(false);

        // The technician is watching. Waiting out the check interval before
        // the folder they have just bound is looked at would make a working
        // binding indistinguishable from a wrong one.
        _wake.Set();

        return written;
    }
}

/// <summary>Asked to write configuration by a machine that has no token.</summary>
public sealed class NotPairedException : Exception
{
    public NotPairedException()
        : base("This machine is not paired with an ADL instance yet.")
    {
    }
}

/// <summary>Asked about a station link this device's configuration does not have.</summary>
public sealed class UnknownStationLinkException : Exception
{
    public UnknownStationLinkException(long stationLinkId)
        : base($"This machine has no station link {stationLinkId}. Its station list may be out of date.")
    {
        StationLinkId = stationLinkId;
    }

    public long StationLinkId { get; }
}
