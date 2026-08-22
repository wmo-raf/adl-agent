using System.Text.Json.Nodes;
using AdlAgent.Core.Update;

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

    /// <summary>
    /// Change one station link's app-tier settings.
    /// </summary>
    /// <remarks>
    /// The one call the agent makes on someone else's behalf: a technician
    /// standing at the machine says where this station's files are and how
    /// they are named, and it is written to ADL rather than kept here.
    /// <para>
    /// The changes travel as they were given rather than as a filled-in
    /// record. Sending a whole <see cref="StationLinkAppConfig"/> would make
    /// every write assert a value for every field, including the ones a newer
    /// ADL knows about and this version of the agent does not -- and each
    /// such write would quietly reset them to this agent's defaults. Naming
    /// only what changed is also what lets ADL answer
    /// <c>read_only_fields</c> with the field the person actually typed.
    /// </para>
    /// </remarks>
    /// <exception cref="DeviceRevokedException">The token is no longer good.</exception>
    /// <exception cref="AdlRequestException">ADL would not write those settings.</exception>
    /// <exception cref="AdlUnreachableException">ADL could not be reached.</exception>
    Task<ConfigWriteResponse> UpdateStationLinkConfigAsync(
        string token,
        long stationLinkId,
        JsonObject changes,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Ask what this machine should be running.
    /// </summary>
    /// <remarks>
    /// The feed is served by this device's own ADL instance and by nothing
    /// else, which is the point: the machines this product exists for cannot
    /// reach the internet, so an update channel anywhere but here would be a
    /// fleet that never updates (story 28).
    /// </remarks>
    /// <param name="tier">
    /// How this install was installed -- see <see cref="Update.UpdateTiers"/>.
    /// The two tiers take different packages, and ADL picks.
    /// </param>
    /// <exception cref="DeviceRevokedException">The token is no longer good.</exception>
    /// <exception cref="AdlRequestException">This instance serves no update feed.</exception>
    /// <exception cref="AdlUnreachableException">ADL could not be reached.</exception>
    Task<UpdateOffer> UpdateOfferAsync(
        string token, string tier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch an offered package to <paramref name="destinationPath"/>.
    /// </summary>
    /// <remarks>
    /// Streamed to disk rather than read into memory -- these are tens of
    /// megabytes and the machines are small -- and refused the moment it
    /// grows past the size ADL stated, so a feed answering with something
    /// else cannot fill a country server's disk before anyone checks its
    /// hash.
    /// </remarks>
    /// <param name="path">The artifact path from the offer, relative to the API base.</param>
    /// <param name="maxBytes">The size ADL promised. Anything longer is refused mid-stream.</param>
    /// <exception cref="DeviceRevokedException">The token is no longer good.</exception>
    /// <exception cref="AdlRequestException">ADL would not serve that package.</exception>
    /// <exception cref="AdlUnreachableException">ADL could not be reached.</exception>
    Task DownloadUpdateAsync(
        string token,
        string path,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken = default);
}
