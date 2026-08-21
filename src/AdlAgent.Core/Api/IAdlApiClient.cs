namespace AdlAgent.Core.Api;

/// <summary>
/// The whole of what the agent asks ADL for, in this release.
/// </summary>
/// <remarks>
/// An interface rather than a class the loops new up, because the seam it
/// draws is the one the tests care about: everything above it can be driven
/// against a fake ADL, and everything below it is HTTP with no decisions in
/// it. The manifest and upload calls join it with the upload cycle.
/// </remarks>
public interface IAdlApiClient
{
    /// <summary>
    /// Trade a pairing code for this machine's token. The one call that
    /// needs no credential, because it is where the credential comes from.
    /// </summary>
    /// <exception cref="AdlRequestException">The code was not redeemable.</exception>
    /// <exception cref="AdlUnreachableException">ADL could not be reached.</exception>
    Task<PairResponse> PairAsync(string pairingCode, CancellationToken cancellationToken = default);

    /// <summary>This device's whole world: connections, stations, both config tiers.</summary>
    /// <exception cref="DeviceRevokedException">The token is no longer good.</exception>
    /// <exception cref="AdlUnreachableException">ADL could not be reached.</exception>
    Task<SyncResponse> SyncAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Say that this machine is alive, and how it is doing.</summary>
    /// <exception cref="DeviceRevokedException">The token is no longer good.</exception>
    /// <exception cref="AdlUnreachableException">ADL could not be reached.</exception>
    Task<HeartbeatResponse> HeartbeatAsync(
        string token, HeartbeatRequest heartbeat, CancellationToken cancellationToken = default);
}
