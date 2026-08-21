namespace AdlAgent.Core.Api;

/// <summary>
/// The whole of what the agent asks ADL for, in this release.
/// </summary>
/// <remarks>
/// An interface rather than a class the loops new up, because the seam it
/// draws is the one the tests care about: everything above it can be driven
/// against a fake ADL, and everything below it is HTTP with no decisions in
/// it.
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

    /// <summary>
    /// Offer a page of candidate files and be told which of them to send.
    /// </summary>
    /// <remarks>
    /// One call for the whole machine, however many stations it serves: these
    /// links are slow and a round trip per station would dominate the cycle.
    /// Pages, because a folder nobody has looked at in a year is not a batch
    /// ADL will accept in one go -- the page size comes from
    /// <see cref="AgentLimits.ManifestEntries"/>.
    /// </remarks>
    /// <exception cref="DeviceRevokedException">The token is no longer good.</exception>
    /// <exception cref="AdlUnreachableException">ADL could not be reached.</exception>
    Task<ManifestResponse> ManifestAsync(
        string token, IReadOnlyList<ManifestEntry> files, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send one file, with the entry that promised it.
    /// </summary>
    /// <remarks>
    /// One file per request, so a failure costs one file and the next
    /// manifest heals it. ADL checks the bytes against the entry and refuses
    /// anything that disagrees -- which is what happens when a vendor process
    /// appended to the file between the hash and the read, and is why a
    /// refusal here is a normal event rather than an error to escalate.
    /// </remarks>
    /// <param name="path">
    /// The file on this machine, opened here and streamed. Never read into
    /// memory: the cap is fifty megabytes and these are small servers.
    /// </param>
    /// <exception cref="DeviceRevokedException">The token is no longer good.</exception>
    /// <exception cref="AdlRequestException">ADL refused this file.</exception>
    /// <exception cref="AdlUnreachableException">ADL could not be reached.</exception>
    Task<UploadResponse> UploadFileAsync(
        string token, ManifestEntry entry, string path, CancellationToken cancellationToken = default);
}
