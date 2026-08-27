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
    private readonly ReconciliationSweep _sweeps;
    private readonly FileHashCache _hashes;
    private readonly IAdlApiClient _client;
    private readonly AgentSession _session;
    private readonly AgentCadence _cadence;
    private readonly CycleReportStore _cycles;
    private readonly CycleConcurrency _concurrency;
    private readonly TimeProvider _time;
    private readonly ILogger<UploadCycle> _logger;

    /// <summary>
    /// The stations being collected right now, and the gate over that set.
    /// </summary>
    /// <remarks>
    /// A claim per station rather than one gate on the machine. The hazard is
    /// narrower than the machine: two passes over <em>one</em> station's
    /// folder would hash every file twice and offer ADL the same manifest
    /// from both. Two passes over two different folders are simply this
    /// machine doing its job.
    /// <para>
    /// A single gate was right while a cycle was one pass over everything and
    /// took seconds. It stopped being right the moment collection ran a unit
    /// at a time: on a machine working through a backlog something is always
    /// running, and a technician standing at it would find "Collect now"
    /// refused for hours on a healthy station that had nothing to do with the
    /// backlog.
    /// </para>
    /// </remarks>
    private readonly HashSet<long> _collecting = [];

    private readonly Lock _gate = new();

    public UploadCycle(
        ConfigurationService configuration,
        FolderScanner scanner,
        ReconciliationSweep sweeps,
        FileHashCache hashes,
        IAdlApiClient client,
        AgentSession session,
        AgentCadence cadence,
        CycleReportStore cycles,
        CycleConcurrency concurrency,
        TimeProvider time,
        ILogger<UploadCycle> logger)
    {
        _configuration = configuration;
        _scanner = scanner;
        _sweeps = sweeps;
        _hashes = hashes;
        _client = client;
        _session = session;
        _cadence = cadence;
        _cycles = cycles;
        _concurrency = concurrency;
        _time = time;
        _logger = logger;
    }

    /// <summary>True while anything on this machine is being collected.</summary>
    /// <remarks>
    /// What the window says out loud, so that a machine grinding through a
    /// backlog does not look like a machine doing nothing -- which is how
    /// this whole thread started. Not what the collect-now command asks:
    /// that asks about one station, through
    /// <see cref="IsCollecting"/>.
    /// </remarks>
    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _collecting.Count > 0;
            }
        }
    }

    /// <summary>How many stations are being collected right now.</summary>
    public int CollectingCount
    {
        get
        {
            lock (_gate)
            {
                return _collecting.Count;
            }
        }
    }

    /// <summary>True while this station in particular is being collected.</summary>
    /// <remarks>
    /// Read by the collect-now command so it can refuse in a sentence rather
    /// than queue behind a pass over the same folder or run beside it. Both
    /// would be worse than the refusal: a queued run starts minutes after the
    /// button, on a window that has been closed, and a concurrent one hashes
    /// the same folder twice and offers ADL the same files from two
    /// manifests.
    /// </remarks>
    public bool IsCollecting(long stationLinkId)
    {
        lock (_gate)
        {
            return _collecting.Contains(stationLinkId);
        }
    }

    /// <summary>
    /// Take every station in <paramref name="stationLinkIds"/>, or none.
    /// </summary>
    /// <remarks>
    /// All or nothing, under one lock. A unit that took the stations it could
    /// and skipped the rest would be collecting half a shared folder while
    /// something else collected the other half, which is the case the claim
    /// exists to stop.
    /// </remarks>
    private bool TryClaim(IReadOnlyCollection<long> stationLinkIds)
    {
        lock (_gate)
        {
            if (stationLinkIds.Any(_collecting.Contains))
            {
                return false;
            }

            foreach (var stationLinkId in stationLinkIds)
            {
                _collecting.Add(stationLinkId);
            }

            return true;
        }
    }

    /// <summary>Take them, waiting for whoever has them now.</summary>
    /// <remarks>
    /// Waiting rather than skipping, because the tick is the thing that must
    /// happen: a station dropped from a tick because somebody was pressing a
    /// button on it is the sort of gap that reaches HQ as a machine that has
    /// quietly stopped. What has changed is the size of the wait -- one unit
    /// waits for one technician's collect, and every other unit on the
    /// machine carries on around it, where a single gate would have made the
    /// whole tick queue.
    /// <para>
    /// Cannot deadlock. Units are disjoint by construction -- that is what
    /// grouping stations by the folders they share is for -- so the only
    /// contention is a unit against the one station somebody is collecting by
    /// hand, and that collect took its claim without waiting for anything.
    /// </para>
    /// </remarks>
    private async Task ClaimAsync(
        IReadOnlyCollection<long> stationLinkIds, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task released;

            lock (_gate)
            {
                if (!stationLinkIds.Any(_collecting.Contains))
                {
                    foreach (var stationLinkId in stationLinkIds)
                    {
                        _collecting.Add(stationLinkId);
                    }

                    return;
                }

                // Read inside the lock, so a release between the test above
                // and the wait below cannot be missed.
                released = _released.Task;
            }

            await released.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Release(IReadOnlyCollection<long> stationLinkIds)
    {
        lock (_gate)
        {
            foreach (var stationLinkId in stationLinkIds)
            {
                _collecting.Remove(stationLinkId);
            }

            // Everyone waiting looks again, and whoever finds their stations
            // free takes them. Woken rather than handed the claim, because
            // what a waiter wants is a set and this does not know whose set
            // has just become whole.
            _released.TrySetResult();
            _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Run one tick to its end, or to the point where it cannot go on.</summary>
    /// <remarks>
    /// The tick is never skipped. It is the thing that must happen; a
    /// technician's button merely brings one station's turn forward, and a
    /// tick silently dropped because somebody was pressing it is the sort of
    /// gap that reaches HQ as a machine that has quietly stopped.
    /// <para>
    /// A station somebody is collecting by hand right now is the one thing
    /// the tick leaves alone -- see <see cref="CollectAsync"/> -- and it is
    /// picked up on the next one, minutes later.
    /// </para>
    /// </remarks>
    public Task RunAsync(CancellationToken cancellationToken = default) =>
        RunEveryStationAsync(cancellationToken);

    private async Task RunEveryStationAsync(CancellationToken cancellationToken)
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

        var now = _time.GetUtcNow();

        // Decided before the scan and recorded after it, so that a cycle
        // which dies in the middle of a sweep leaves the sweep still owed.
        var sweep = _sweeps.Plan(configuration, now);

        // The tick's shape, decided before a single folder is read. Each unit
        // is a station and whatever it shares a folder with -- one station and
        // one folder, for almost every station in a fleet.
        var units = _scanner.Plan(configuration, sweep, now);

        // One count of uploads for the whole machine, not one per unit. Eight
        // units each sending four files would be thirty-two on the wire.
        using var uploads = new UploadSlots(
            _concurrency.UploadsFor(configuration.Sync.Limits));

        // Cancelled by whichever unit first finds ADL gone. Linked to the
        // caller's, so a service shutdown still stops everything -- and told
        // apart from it below, because one of the two is a reason to record
        // what a unit had done and the other is not.
        using var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await Parallel.ForEachAsync(
            units,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _concurrency.Units,
                CancellationToken = cancellationToken,
            },
            async (unit, _) =>
            {
                if (tick.IsCancellationRequested)
                {
                    // ADL went while this unit was still queued. It has not
                    // run, so it has nothing to report -- reporting a pass
                    // that never happened would be worse than the silence.
                    return;
                }

                if (!await CollectAsync(
                        token, configuration, unit, uploads, tick.Token, cancellationToken)
                        .ConfigureAwait(false))
                {
                    // Not this unit's problem: every unit's. The rest of the
                    // tick is abandoned rather than spent discovering the
                    // same thing once per folder on a link already down.
                    await tick.CancelAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        var cutShort = tick.IsCancellationRequested;

        // Once for the tick, not once per unit. The cache forgets whatever it
        // was not asked about since the last call, so a per-unit sweep of it
        // would throw away every other unit's working set.
        _hashes.Forget();

        if (cutShort)
        {
            return;
        }

        // This tick went round everything the machine has. The units have
        // already stamped their own completions, so on an ordinary tick this
        // agrees with the last of them; what it is here for is the machine
        // with no units at all -- every station switched off in ADL, or none
        // linked yet -- which completes a pass over an empty fleet every
        // check interval and is perfectly healthy.
        _cycles.Finished(_time.GetUtcNow());

        // Left to the end, and only on a tick that finished, because both
        // answer "which stations has this machine still got?" -- a question
        // about the whole fleet rather than about any one unit. A
        // configuration served from cache to a machine that could not reach
        // ADL is not the fleet; it is the last fleet anybody saw.
        _sweeps.Prune(sweep);

        if (sweep.Prunes)
        {
            // Without it the reported picture would be every station this
            // device has ever been given, and stations moved to another
            // machine months ago would go on reporting their last counts and
            // their backlog to ADL for the life of the service.
            _cycles.Prune(sweep.Known);
        }
    }

    /// <summary>
    /// Scan one unit, offer what it found, and record what it came to.
    /// </summary>
    /// <remarks>
    /// The unit is the thing that finishes. Its counts, its sweep and its
    /// completion are recorded here rather than at the end of a pass over the
    /// whole machine, which is what stops a station's report from waiting on
    /// a folder it has nothing to do with -- and stops ADL reading a machine
    /// that is uploading hard as a machine that has stopped
    /// (wmo-raf/adl#303, wmo-raf/adl#304).
    /// </remarks>
    /// <returns>False when ADL stopped answering and the tick cannot go on.</returns>
    private async Task<bool> CollectAsync(
        string token,
        AgentConfiguration configuration,
        FolderScanner.ScanUnit unit,
        UploadSlots uploads,
        CancellationToken cancellationToken,
        CancellationToken stopping)
    {
        // Waits if somebody at the machine is collecting one of these
        // stations by hand. The tick does not give up its turn; it takes it
        // late, and the other units carry on meanwhile.
        await ClaimAsync(unit.StationLinkIds, cancellationToken).ConfigureAwait(false);

        try
        {
            return await CollectClaimedAsync(
                token, configuration, unit, uploads, cancellationToken, stopping)
                .ConfigureAwait(false);
        }
        finally
        {
            Release(unit.StationLinkIds);
        }
    }

    private async Task<bool> CollectClaimedAsync(
        string token,
        AgentConfiguration configuration,
        FolderScanner.ScanUnit unit,
        UploadSlots uploads,
        CancellationToken cancellationToken,
        CancellationToken stopping)
    {
        var scan = _scanner.Scan(unit, _time.GetUtcNow());

        bool delivered;

        try
        {
            delivered = await DeliverAsync(token, configuration, scan, uploads, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            // Another unit found ADL gone and stopped the tick out from under
            // this one. Cut short exactly as if this unit had found it
            // itself: what it managed is still worth recording, and its
            // completion still is not.
            delivered = false;
        }

        var at = _time.GetUtcNow();

        if (delivered)
        {
            // The station offered everything it could, so its day's
            // reconciliation is spent. A unit cut short offered some of its
            // folder and not the rest, and recording that as done would leave
            // the unoffered part waiting another day for no reason.
            _sweeps.Record(scan.Reconciled, at);
        }

        // Recorded either way, and completion only on the first. A unit that
        // died mid-page still knows what it scanned and why it stopped, and
        // that sentence is the only thing standing between an operator and a
        // station showing "no cycle yet" for ever. What it must not do is
        // move the completion mark: a machine whose every pass is cut short
        // is exactly the machine ADL is meant to call stuck.
        Record(scan, at, completed: delivered);

        return delivered;
    }

    /// <summary>
    /// Run a cycle for one station, now, because somebody at the machine
    /// asked.
    /// </summary>
    /// <remarks>
    /// The same four steps as a scheduled cycle -- sync, scan, offer, send --
    /// over a configuration narrowed to one station link. Narrowing the
    /// configuration rather than filtering inside the scan is what keeps this
    /// from being a second implementation of the cycle: the sweep planner, the
    /// scanner, the pager and the uploader are the ones the loop uses,
    /// unchanged, and a station collected this way is collected exactly as it
    /// would have been an hour later.
    /// <para>
    /// It always sweeps. The reason somebody presses this button is almost
    /// always that they have just put files in the folder, and a backfill
    /// copied in with its original timestamps preserved is invisible to the
    /// candidate window -- so a collect-now that only looked at the window
    /// would report "nothing new" to the one person who knows there is.
    /// </para>
    /// <para>
    /// The result is returned rather than recorded as a cycle. See
    /// <see cref="RequestedCollect"/>.
    /// </para>
    /// </remarks>
    /// <returns>
    /// What the run came to, or null when this station is already being
    /// collected.
    /// </returns>
    /// <remarks>
    /// Refused only for <em>this</em> station. A technician collecting
    /// Garissa is blocked by another pass over Garissa's own folder and by
    /// nothing else -- not by Kisumu's year of backlog, which is what a
    /// single gate on the machine would have made them wait for.
    /// </remarks>
    public async Task<RequestedCollect?> CollectStationAsync(
        long stationLinkId, ICollectWatcher watcher, CancellationToken cancellationToken)
    {
        // Claimed before anything is read, and on the station the button
        // names -- which is the whole of this run, because the configuration
        // is narrowed to that one link before it is planned. Its neighbours
        // in a shared dump directory are not collected here, and the
        // scheduled unit that holds all of them cannot start while this claim
        // stands.
        if (!TryClaim([stationLinkId]))
        {
            return null;
        }

        try
        {
            return await CollectOneAsync(stationLinkId, watcher, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Release([stationLinkId]);
        }
    }

    private async Task<RequestedCollect> CollectOneAsync(
        long stationLinkId, ICollectWatcher watcher, CancellationToken cancellationToken)
    {
        watcher.Step("Asking ADL for the latest configuration…");

        var configuration = await SyncAsync(cancellationToken).ConfigureAwait(false);
        var token = _session.ActiveToken;

        if (configuration is null)
        {
            return Came(watcher, "This machine has no configuration to work from yet.");
        }

        if (token is null)
        {
            return Came(watcher, "This machine is not paired with ADL, so nothing can be sent.");
        }

        if (Only(configuration, stationLinkId) is not { } narrowed)
        {
            // The configuration moved under the button: HQ unlinked the
            // station between the window drawing the row and somebody
            // pressing the item on it.
            return Came(watcher, UnknownStationLinkException.Describe(stationLinkId));
        }

        var now = _time.GetUtcNow();

        // Never mind whether this station is due one. Due-ness is about
        // spending a daily budget wisely, and a person standing at the machine
        // asking for this station now is a better reason than the clock.
        var sweep = new SweepPlan(
            new HashSet<long> { stationLinkId },
            new HashSet<long> { stationLinkId })
        {
            Prunes = false,
        };

        watcher.Step("Scanning the folder…");

        // Through the same seam as a scheduled tick, and for the same reason
        // narrowing the configuration was chosen over filtering inside the
        // scan: a station collected this way is collected exactly as it would
        // have been an hour later. A configuration holding one station plans
        // to one unit -- there is nothing left for it to share a folder with.
        var scan = _scanner.Plan(narrowed, sweep, now) is [var only]
            ? _scanner.Scan(only, now)
            : throw new InvalidOperationException(
                "A configuration narrowed to one station planned to more than one unit.");

        // Before the delivery rather than after it, because the counts move
        // during the delivery and this is what the window watching reads them
        // through.
        watcher.Counting(scan.For(stationLinkId));

        watcher.Step("Offering what was found to ADL…");

        using var uploads = new UploadSlots(
            _concurrency.UploadsFor(narrowed.Sync.Limits));

        var delivered = await DeliverAsync(token, narrowed, scan, uploads, cancellationToken)
            .ConfigureAwait(false);

        // Cleared whether or not the delivery finished. Unlike the scheduled
        // cycle's, this cache holds one station's working set, and the next
        // thing to walk this folder is a full cycle that has its own opinion
        // about what is in it.
        _hashes.Forget();

        if (delivered)
        {
            // The sweep is recorded on this run alone, so the station's next
            // scheduled sweep is a day from now rather than a day from
            // whenever the loop last got to it. One just happened.
            //
            // Recorded and never pruned. This plan knows one station, and
            // every other station on the machine is absent from it because
            // nobody asked rather than because it has gone.
            _sweeps.Record(scan.Reconciled, now);
        }

        var tally = scan.For(stationLinkId);

        return new RequestedCollect
        {
            At = _time.GetUtcNow(),
            Scanned = tally?.Scanned ?? 0,
            Offered = tally?.Offered ?? 0,
            Uploaded = tally?.Uploaded ?? 0,
            Failed = tally?.Failed ?? 0,
            Cancelled = cancellationToken.IsCancellationRequested,
            Error = tally?.Error
                ?? (delivered ? null : "ADL stopped answering before this station finished."),
        };
    }

    /// <summary>
    /// This configuration with everything but one station link taken out of
    /// it, or null when it holds no such link.
    /// </summary>
    /// <remarks>
    /// The connection is kept around the link rather than the link lifted out
    /// of it, because the connection's own enabled flag is what the scanner
    /// and the sweep planner both read first. A link hoisted into a connection
    /// invented here would be collected from a connection HQ has switched off.
    /// </remarks>
    private static AgentConfiguration? Only(AgentConfiguration configuration, long stationLinkId)
    {
        foreach (var connection in configuration.Sync.Connections)
        {
            var link = connection.StationLinks
                .FirstOrDefault(candidate => candidate.Id == stationLinkId);

            if (link is null)
            {
                continue;
            }

            return configuration with
            {
                Sync = configuration.Sync with
                {
                    Connections = [connection with { StationLinks = [link] }],
                },
            };
        }

        return null;
    }

    /// <summary>A run that could not start, as the result it is.</summary>
    private RequestedCollect Came(ICollectWatcher watcher, string problem)
    {
        watcher.Step(problem);

        return new RequestedCollect { At = _time.GetUtcNow(), Error = problem };
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
        UploadSlots uploads,
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

            if (!await SendAsync(token, scan, page, answer, uploads, cancellationToken).ConfigureAwait(false))
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
    /// <remarks>
    /// Several at a time, up to what ADL allows the machine across all of its
    /// units. Three thousand files at one round trip each is the difference
    /// between a station catching up this morning and catching up this week,
    /// and the round trip is nearly all waiting -- so the link is used rather
    /// than taken turns on.
    /// <para>
    /// The counters this writes are one station's, written from several
    /// uploads at once, which is what <see cref="LinkTally"/>'s interlocking
    /// is for. Nothing else here is shared: the page is read-only and the
    /// scan's tally lookup is a finished dictionary.
    /// </para>
    /// </remarks>
    /// <returns>False when ADL stopped answering.</returns>
    private async Task<bool> SendAsync(
        string token,
        ScanResult scan,
        IReadOnlyList<FileCandidate> page,
        ManifestResponse answer,
        UploadSlots uploads,
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

        // Set by whichever upload finds ADL gone, and read once at the end.
        // The rest of the page stops rather than each file discovering the
        // same dead link for itself.
        var ended = 0;

        using var ending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await Parallel.ForEachAsync(
                answer.Requested,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = uploads.Most,
                    CancellationToken = ending.Token,
                },
                async (wanted, token1) =>
                {
                    if (!offered.TryGetValue((wanted.StationLinkId, wanted.Name), out var candidate))
                    {
                        // ADL asked for something this page did not offer. Not
                        // worth a failure against the station: the next manifest
                        // will offer whatever it can actually see.
                        _logger.LogWarning(
                            "ADL asked for {Name} on station link {Link}, which was not in the manifest it answered.",
                            wanted.Name, wanted.StationLinkId);

                        return;
                    }

                    var tally = scan.For(wanted.StationLinkId);

                    // Held across the upload and nothing else, so the bound is
                    // on what is on the wire rather than on how many files the
                    // machine is thinking about.
                    using var slot = await uploads.TakeAsync(token1).ConfigureAwait(false);

                    try
                    {
                        var stored = await _client
                            .UploadFileAsync(token, candidate.Entry, candidate.Path, token1)
                            .ConfigureAwait(false);

                        tally?.Accept();

                        _logger.LogDebug(
                            "ADL took {Name} for station link {Link}: {Size} bytes, {Status}.",
                            stored.Name, stored.StationLinkId, stored.Size, stored.Status);
                    }
                    catch (Exception exception) when (EndsTheCycle(exception))
                    {
                        Stop(exception, "An upload");

                        Interlocked.Exchange(ref ended, 1);

                        await ending.CancelAsync().ConfigureAwait(false);
                    }
                    catch (AdlRequestException exception)
                    {
                        // One file, refused. The commonest reason is the honest
                        // one: the vendor appended to it between the hash and the
                        // read, so the bytes no longer match what was promised.
                        // Next cycle offers the file as it now stands.
                        tally?.Fail($"{candidate.Entry.Name}: {exception.Detail}");
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        // Gone, moved, or locked since the scan. Also next
                        // cycle's business, if it is still there at all.
                        tally?.Fail($"{candidate.Entry.Name}: {exception.Message}");
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref ended) == 1)
        {
            // The page cut itself short, which is the one cancellation this
            // knows the reason for. Anything else -- a service shutting
            // down -- is not this method's to swallow.
        }

        return Volatile.Read(ref ended) == 0;
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
    private void Record(ScanResult scan, DateTimeOffset at, bool completed)
    {
        var links = scan.Links.Values
            .OrderBy(tally => tally.StationLinkId)
            .ToList();

        _cycles.Record(new CycleUnitReport
        {
            At = at,
            Completed = completed,
            Links = links.Select(tally => tally.ToReport()).ToList(),
            Backlogs = links.ToDictionary(
                tally => tally.StationLinkId, tally => tally.Backlog),
        });

        _logger.LogDebug(
            completed
                ? "Unit finished: {Scanned} file(s) scanned, {Offered} offered, {Uploaded} uploaded."
                : "Unit cut short: {Scanned} file(s) scanned, {Offered} offered, {Uploaded} uploaded.",
            links.Sum(tally => tally.Scanned),
            links.Sum(tally => tally.Offered),
            links.Sum(tally => tally.Uploaded));
    }
}
