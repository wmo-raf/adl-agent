using AdlAgent.Core.Api;
using AdlAgent.Core.Configuration;
using AdlAgent.Core.Heartbeat;
using AdlAgent.Core.Hosting;
using AdlAgent.Core.Pairing;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Cycle;

/// <summary>
/// One pass of the thing this product is: sync, scan, offer, send.
/// </summary>
/// <remarks>
/// The agent remembers nothing about what it has delivered. The vendor's
/// folder is its only state, and a folder cannot remember -- so every cycle
/// offers what it can see and is told which of those files ADL wants
/// (decision #266). Everything that could go wrong in the middle is therefore
/// survivable by doing nothing: a cycle that dies between the manifest and
/// the uploads leaves ADL believing exactly what it believed before, and the
/// next cycle offers the same files again. There is no queue to drain, no
/// retry table to keep, and nothing to reconcile after a power cut.
/// <para>
/// Uploads run page by page rather than after the whole manifest, so that on
/// a fresh install facing months of backlog the newest files -- which are in
/// the first page, because the scan sorts newest first -- are on their way to
/// ADL before the second page has been offered.
/// </para>
/// </remarks>
public sealed class UploadCycle
{
    private readonly ConfigurationService _configuration;
    private readonly FolderScanner _scanner;
    private readonly FileHashCache _hashes;
    private readonly IAdlApiClient _client;
    private readonly AgentSession _session;
    private readonly AgentCadence _cadence;
    private readonly CycleReportStore _cycles;
    private readonly TimeProvider _time;
    private readonly ILogger<UploadCycle> _logger;

    public UploadCycle(
        ConfigurationService configuration,
        FolderScanner scanner,
        FileHashCache hashes,
        IAdlApiClient client,
        AgentSession session,
        AgentCadence cadence,
        CycleReportStore cycles,
        TimeProvider time,
        ILogger<UploadCycle> logger)
    {
        _configuration = configuration;
        _scanner = scanner;
        _hashes = hashes;
        _client = client;
        _session = session;
        _cadence = cadence;
        _cycles = cycles;
        _time = time;
        _logger = logger;
    }

    /// <summary>Run one cycle to its end, or to the point where it cannot go on.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await SyncAsync(cancellationToken).ConfigureAwait(false);

        // Read after the sync, not before: a sync that came back 401 has just
        // taken this machine's token away, and the whole point of that answer
        // is that nothing more is sent.
        var token = _session.ActiveToken;

        if (configuration is null || token is null)
        {
            // Nothing to work from, or nobody to work for. Neither is a
            // cycle, so neither is reported as one.
            return;
        }

        var scan = _scanner.Scan(configuration, _time.GetUtcNow());

        _hashes.Forget();

        var delivered = await DeliverAsync(token, configuration, scan, cancellationToken)
            .ConfigureAwait(false);

        if (!delivered)
        {
            // Cut short by an ADL that stopped answering. Not recorded as a
            // completed cycle -- and there is nowhere to send the report
            // anyway, since the heartbeat is going down the same link.
            return;
        }

        Record(scan);
    }

    /// <summary>
    /// The top of the cycle: re-read this device's world, and follow ADL's
    /// cadences while it is being told them.
    /// </summary>
    private async Task<AgentConfiguration?> SyncAsync(CancellationToken cancellationToken)
    {
        var configuration = await _configuration.RefreshAsync(cancellationToken).ConfigureAwait(false);

        if (configuration is not null && !configuration.FromCache)
        {
            _cadence.Adopt(
                heartbeatMinutes: configuration.HeartbeatIntervalMinutes,
                checkMinutes: configuration.CheckIntervalMinutes);
        }

        return configuration;
    }

    /// <summary>
    /// Offer the candidates a page at a time and send what is asked for.
    /// </summary>
    /// <returns>False when ADL stopped answering and the cycle was cut short.</returns>
    private async Task<bool> DeliverAsync(
        string token,
        AgentConfiguration configuration,
        ScanResult scan,
        CancellationToken cancellationToken)
    {
        var pageSize = PageSize(configuration.Sync.Limits);

        for (var start = 0; start < scan.Candidates.Count; start += pageSize)
        {
            var page = scan.Candidates
                .Skip(start)
                .Take(pageSize)
                .ToList();

            ManifestResponse answer;

            try
            {
                answer = await _client
                    .ManifestAsync(token, page.Select(candidate => candidate.Entry).ToList(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DeviceRevokedException)
            {
                _session.MarkRevoked();

                return false;
            }
            catch (AdlUnreachableException exception)
            {
                _logger.LogWarning("The manifest did not reach ADL: {Reason}", exception.Message);

                return false;
            }
            catch (AdlRequestException exception)
            {
                // ADL refused the page itself -- too many entries, or an
                // entry it could not read. Nothing in it was accepted, so the
                // whole page waits for the next cycle.
                _logger.LogError(
                    "ADL refused a manifest of {Count} files ({Code}): {Detail}",
                    page.Count, exception.Code, exception.Detail);

                Blame(scan, page, exception.Detail);

                continue;
            }

            Tally(scan, page, answer);

            if (!await SendAsync(token, scan, page, answer, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Send every file ADL asked for out of this page.</summary>
    /// <returns>False when ADL stopped answering.</returns>
    private async Task<bool> SendAsync(
        string token,
        ScanResult scan,
        IReadOnlyList<FileCandidate> page,
        ManifestResponse answer,
        CancellationToken cancellationToken)
    {
        // Built with TryAdd rather than ToDictionary: the key comes from a
        // filesystem this process does not own, and a folder that somehow
        // yielded one name twice would take the whole cycle down over a
        // duplicate key.
        var offered = new Dictionary<(long, string), FileCandidate>();

        foreach (var candidate in page)
        {
            offered.TryAdd((candidate.Entry.StationLinkId, candidate.Entry.Name), candidate);
        }

        foreach (var wanted in answer.Requested)
        {
            if (!offered.TryGetValue((wanted.StationLinkId, wanted.Name), out var candidate))
            {
                // ADL asked for something this page did not offer. Not worth
                // a failure against the station: the next manifest will offer
                // whatever it can actually see.
                _logger.LogWarning(
                    "ADL asked for {Name} on station link {Link}, which was not in the manifest it answered.",
                    wanted.Name, wanted.StationLinkId);

                continue;
            }

            var tally = scan.Links.GetValueOrDefault(wanted.StationLinkId);

            try
            {
                await _client
                    .UploadFileAsync(token, candidate.Entry, candidate.Path, cancellationToken)
                    .ConfigureAwait(false);

                if (tally is not null)
                {
                    tally.Uploaded++;
                }
            }
            catch (DeviceRevokedException)
            {
                _session.MarkRevoked();

                return false;
            }
            catch (AdlUnreachableException exception)
            {
                _logger.LogWarning("An upload did not reach ADL: {Reason}", exception.Message);

                return false;
            }
            catch (AdlRequestException exception)
            {
                // One file, refused. The commonest reason is the honest one:
                // the vendor appended to it between the hash and the read, so
                // the bytes no longer match what was promised. Next cycle
                // offers the file as it now stands.
                Fail(tally, candidate, exception.Detail);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Gone, moved, or locked since the scan. Also next cycle's
                // business, if it is still there at all.
                Fail(tally, candidate, exception.Message);
            }
        }

        return true;
    }

    /// <summary>Note what ADL made of this page, per station.</summary>
    private void Tally(ScanResult scan, IReadOnlyList<FileCandidate> page, ManifestResponse answer)
    {
        foreach (var candidate in page)
        {
            if (scan.Links.TryGetValue(candidate.Entry.StationLinkId, out var tally))
            {
                tally.Offered++;
            }
        }

        foreach (var wanted in answer.Requested)
        {
            if (scan.Links.TryGetValue(wanted.StationLinkId, out var tally))
            {
                tally.Requested++;
            }
        }

        // A machine works from a cached configuration, so it will sometimes
        // offer files for a station that has since been moved to another
        // device or switched off. Said out loud per station, because "ADL is
        // ignoring Garissa" is something a technician can act on.
        foreach (var stationLinkId in answer.UnknownStationLinks)
        {
            Note(scan, stationLinkId, "ADL does not know this station as one of this machine's.");
        }

        foreach (var stationLinkId in answer.DisabledStationLinks)
        {
            Note(scan, stationLinkId, "This station is switched off in ADL and is not taking files.");
        }
    }

    /// <summary>A page ADL refused outright: every station in it failed by it.</summary>
    private static void Blame(ScanResult scan, IReadOnlyList<FileCandidate> page, string detail)
    {
        foreach (var candidate in page)
        {
            if (scan.Links.TryGetValue(candidate.Entry.StationLinkId, out var tally))
            {
                tally.Failed++;
                tally.Note(detail);
            }
        }
    }

    private static void Fail(LinkTally? tally, FileCandidate candidate, string detail)
    {
        if (tally is null)
        {
            return;
        }

        tally.Failed++;
        tally.Note($"{candidate.Entry.Name}: {detail}");
    }

    private static void Note(ScanResult scan, long stationLinkId, string message)
    {
        if (scan.Links.TryGetValue(stationLinkId, out var tally))
        {
            tally.Note(message);
        }
    }

    /// <summary>
    /// How many candidates go in one manifest call -- ADL's number, clamped.
    /// </summary>
    /// <remarks>
    /// Clamped because a fleet is not worth risking on one bad number: a page
    /// size of zero would loop forever offering nothing, and one far above
    /// what ADL accepts would have every page refused. The upper bound is
    /// generous enough that a real change to the limit is followed.
    /// </remarks>
    private static int PageSize(AgentLimits limits) => Math.Clamp(limits.ManifestEntries, 1, 5_000);

    /// <summary>Leave the cycle where the heartbeat will find it.</summary>
    private void Record(ScanResult scan)
    {
        var links = scan.Links.Values
            .OrderBy(tally => tally.StationLinkId)
            .ToList();

        _cycles.Record(
            new CycleReport
            {
                CompletedAt = _time.GetUtcNow(),
                Links = links.Select(tally => tally.ToReport()).ToList(),
            },
            backlogCount: links.Sum(tally => tally.Backlog));

        _logger.LogDebug(
            "Cycle finished: {Scanned} file(s) scanned, {Offered} offered, {Uploaded} uploaded.",
            links.Sum(tally => tally.Scanned),
            links.Sum(tally => tally.Offered),
            links.Sum(tally => tally.Uploaded));
    }
}
