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

        var delivered = await DeliverAsync(token, configuration, scan, cancellationToken)
            .ConfigureAwait(false);

        if (!delivered)
        {
            // Cut short by an ADL that stopped answering. Not recorded as a
            // completed cycle -- and there is nowhere to send the report
            // anyway, since the heartbeat is going down the same link.
            return;
        }

        // Only now: the files are read as the pages are built, so until the
        // last page has gone out the cache does not yet know what this
        // cycle's working set was.
        _hashes.Forget();

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
    /// <remarks>
    /// A page ADL refuses is not simply reported and dropped. The scan is
    /// deterministic, so the identical page would be built again next cycle
    /// and refused again -- for ever. One filename ADL cannot store would
    /// take its four hundred and ninety-nine blameless neighbours with it,
    /// and the station would go quiet with nothing but a repeating message to
    /// show for it. So a refusal is answered by making the page one ADL can
    /// read: smaller when it was too long, and without the entries ADL named
    /// when it could not read them. Every branch either shrinks the page or
    /// gives up on part of it, so the loop always moves.
    /// </remarks>
    /// <returns>False when ADL stopped answering and the cycle was cut short.</returns>
    private async Task<bool> DeliverAsync(
        string token,
        AgentConfiguration configuration,
        ScanResult scan,
        CancellationToken cancellationToken)
    {
        var pageSize = PageSize(configuration.Sync.Limits);

        using var candidates = scan.Candidates.GetEnumerator();

        // Candidates taken from the sequence but not yet accepted for a page,
        // because the page they were in came back refused.
        var carried = new List<FileCandidate>();

        while (true)
        {
            var page = NextPage(candidates, carried, pageSize);

            if (page.Count == 0)
            {
                return true;
            }

            ManifestResponse answer;

            try
            {
                answer = await _client
                    .ManifestAsync(token, page.Select(candidate => candidate.Entry).ToList(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (EndsTheCycle(exception))
            {
                Stop(exception, "The manifest");

                return false;
            }
            catch (AdlRequestException exception)
            {
                if (exception.Code == AgentErrorCodes.ManifestTooLarge && pageSize > 1)
                {
                    // ADL accepts fewer than it said it would. Halve and try
                    // the same files again rather than spend every cycle
                    // being told the same thing.
                    pageSize = Math.Max(1, pageSize / 2);

                    _logger.LogWarning(
                        "ADL refused a manifest of {Count} files as too large; offering {PageSize} at a time.",
                        page.Count, pageSize);

                    carried.InsertRange(0, page);

                    continue;
                }

                if (Narrow(scan, page, exception) is { } readable)
                {
                    // ADL named the entries it could not read. The rest of
                    // the page is still worth offering, and offering it now
                    // is what keeps one bad filename from silencing a folder.
                    carried.InsertRange(0, readable);

                    continue;
                }

                _logger.LogError(
                    "ADL refused a manifest of {Count} files ({Code}): {Detail}",
                    page.Count, exception.Code, exception.Detail);

                foreach (var candidate in page)
                {
                    scan.For(candidate.Entry.StationLinkId)?.Fail(exception.Detail);
                }

                continue;
            }

            Tally(scan, page, answer);

            if (!await SendAsync(token, scan, page, answer, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The page without the entries ADL named, or <c>null</c> if that is no
    /// help.
    /// </summary>
    /// <remarks>
    /// Null when ADL named nothing, named every entry, or named only
    /// positions this page does not have: in each case there is no smaller
    /// page worth trying, and the caller gives up on the whole of it and
    /// charges it once. Nothing is charged here until that is decided --
    /// blaming a file on the way past and then blaming the whole page it was
    /// in would have every station report twice the failures it had.
    /// </remarks>
    private static List<FileCandidate>? Narrow(
        ScanResult scan, List<FileCandidate> page, AdlRequestException exception)
    {
        var dropped = new HashSet<int>();

        foreach (var rejected in exception.Rejected)
        {
            if (rejected.Index >= 0 && rejected.Index < page.Count)
            {
                dropped.Add(rejected.Index);
            }
        }

        if (dropped.Count == 0 || dropped.Count == page.Count)
        {
            return null;
        }

        foreach (var rejected in exception.Rejected.Where(rejected => dropped.Contains(rejected.Index)))
        {
            var candidate = page[rejected.Index];

            // The only place anyone will ever see why one file of five
            // hundred is not arriving, so it is ADL's own sentence about it.
            scan.For(candidate.Entry.StationLinkId)
                ?.Fail($"{candidate.Entry.Name}: {rejected.Detail}");
        }

        return page.Where((_, index) => !dropped.Contains(index)).ToList();
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

            var tally = scan.For(wanted.StationLinkId);

            try
            {
                var stored = await _client
                    .UploadFileAsync(token, candidate.Entry, candidate.Path, cancellationToken)
                    .ConfigureAwait(false);

                tally?.Accept();

                _logger.LogDebug(
                    "ADL took {Name} for station link {Link}: {Size} bytes, {Status}.",
                    stored.Name, stored.StationLinkId, stored.Size, stored.Status);
            }
            catch (Exception exception) when (EndsTheCycle(exception))
            {
                Stop(exception, "An upload");

                return false;
            }
            catch (AdlRequestException exception)
            {
                // One file, refused. The commonest reason is the honest one:
                // the vendor appended to it between the hash and the read, so
                // the bytes no longer match what was promised. Next cycle
                // offers the file as it now stands.
                tally?.Fail($"{candidate.Entry.Name}: {exception.Detail}");
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Gone, moved, or locked since the scan. Also next cycle's
                // business, if it is still there at all.
                tally?.Fail($"{candidate.Entry.Name}: {exception.Message}");
            }
        }

        return true;
    }

    /// <summary>Note what ADL made of this page, per station.</summary>
    private static void Tally(
        ScanResult scan, IReadOnlyList<FileCandidate> page, ManifestResponse answer)
    {
        foreach (var candidate in page)
        {
            scan.For(candidate.Entry.StationLinkId)?.Offer();
        }

        foreach (var wanted in answer.Requested)
        {
            scan.For(wanted.StationLinkId)?.Want();
        }

        // A machine works from a cached configuration, so it will sometimes
        // offer files for a station that has since been moved to another
        // device or switched off. Said out loud per station, because "ADL is
        // ignoring Garissa" is something a technician can act on.
        foreach (var stationLinkId in answer.UnknownStationLinks)
        {
            scan.Note(stationLinkId, "ADL does not know this station as one of this machine's.");
        }

        foreach (var stationLinkId in answer.DisabledStationLinks)
        {
            scan.Note(stationLinkId, "This station is switched off in ADL and is not taking files.");
        }
    }

    /// <summary>
    /// True when this failure ends the cycle rather than costing one file.
    /// </summary>
    /// <remarks>
    /// The two that do are the two the agent cannot work around by trying the
    /// next file: a token ADL has stopped accepting, and an ADL that is not
    /// answering at all. Everything else -- a refused page, a refused file, a
    /// file that moved -- leaves the rest of the cycle worth running.
    /// </remarks>
    private static bool EndsTheCycle(Exception exception) =>
        exception is DeviceRevokedException or AdlUnreachableException;

    /// <summary>Give up on the rest of this cycle, and say why.</summary>
    private void Stop(Exception exception, string what)
    {
        if (exception is DeviceRevokedException)
        {
            // Withdraws the token, so nothing else this machine does can
            // reach ADL until somebody pairs it again.
            _session.MarkRevoked();

            return;
        }

        _logger.LogWarning("{What} did not reach ADL: {Reason}", what, exception.Message);
    }

    /// <summary>
    /// The next page: what a refused page left over, then whatever the scan
    /// hands over next.
    /// </summary>
    /// <remarks>
    /// Taking from the sequence is what reads and hashes the files, so a page
    /// costs a page's worth of reading and no more. Left-overs come first, so
    /// that narrowing a refused page does not put its survivors behind the
    /// year of history queued up after them.
    /// </remarks>
    private static List<FileCandidate> NextPage(
        IEnumerator<FileCandidate> candidates, List<FileCandidate> carried, int size)
    {
        var page = new List<FileCandidate>(Math.Min(size, 512));

        while (page.Count < size && carried.Count > 0)
        {
            page.Add(carried[0]);
            carried.RemoveAt(0);
        }

        while (page.Count < size && candidates.MoveNext())
        {
            page.Add(candidates.Current);
        }

        return page;
    }

    /// <summary>
    /// How many candidates go in one manifest call -- ADL's number, clamped.
    /// </summary>
    /// <remarks>
    /// Clamped only against a number that could not be a page size at all: a
    /// zero would build empty pages for ever. The upper bound is generous
    /// rather than pinned to what ADL accepts today, because pinning it would
    /// stop a fleet following a real increase -- and a number too high costs
    /// one refusal, which <see cref="DeliverAsync"/> answers by halving until
    /// the pages fit.
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
