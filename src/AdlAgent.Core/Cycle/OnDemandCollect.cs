using System.Collections.Concurrent;
using AdlAgent.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Cycle;

/// <summary>
/// The collect a technician asks for at the machine: starting one, watching
/// it, stopping it, and what the last one came to.
/// </summary>
/// <remarks>
/// One at a time, and only when no scheduled cycle is running. Both are
/// <see cref="UploadCycle"/>'s rule rather than this class's -- there is one
/// gate on the machine and it lives with the thing it guards -- and this is
/// where the refusal becomes a sentence somebody reads.
/// <para>
/// The run is started and not awaited. The control surface serves one client
/// at a time, so a command that waited for a cycle to finish would freeze the
/// tray's own status poll for the length of an upload; instead the command
/// returns as soon as the run is under way, and the window watching it asks
/// again a second later. That is also what makes Cancel possible at all: a
/// held connection has nowhere for a second command to arrive.
/// </para>
/// <para>
/// Nothing here is written to disk. A run that a power cut interrupts is a run
/// nobody is watching any more, and the vendor's folder is still the only
/// state this product keeps.
/// </para>
/// </remarks>
public sealed class OnDemandCollect
{
    private readonly UploadCycle _cycle;
    private readonly ConfigurationService _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<OnDemandCollect> _logger;
    private readonly Lock _gate = new();

    /// <summary>
    /// What each station's last requested collect came to.
    /// </summary>
    /// <remarks>
    /// Kept per station rather than as one last-run, because a technician
    /// working down a list of four broken stations collects each in turn and
    /// then reads the grid. A single slot would leave three rows showing
    /// nothing and no way to tell that from three rows never collected.
    /// </remarks>
    private readonly ConcurrentDictionary<long, RequestedCollect> _results = new();

    private Run? _run;

    public OnDemandCollect(
        UploadCycle cycle,
        ConfigurationService configuration,
        TimeProvider time,
        ILogger<OnDemandCollect> logger)
    {
        _cycle = cycle;
        _configuration = configuration;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Start collecting one station, or say why not.
    /// </summary>
    /// <remarks>
    /// Every refusal here is a sentence rather than a code, because every one
    /// of them is read by the person who pressed the item and none of them is
    /// something a UI switches on. The three are the three ways the button can
    /// be pressed on a station that cannot be collected: HQ has it switched
    /// off, nobody has bound it a folder yet, or a cycle is already running.
    /// </remarks>
    public CollectStarted Start(long stationLinkId)
    {
        var link = _configuration.Current?.StationLinks
            .FirstOrDefault(candidate => candidate.Id == stationLinkId);

        if (link is null)
        {
            return CollectStarted.No(UnknownStationLinkException.Describe(stationLinkId));
        }

        var connection = _configuration.Current?.Sync.Connections
            .FirstOrDefault(candidate => candidate.StationLinks.Any(each => each.Id == stationLinkId));

        if (connection?.Admin.Enabled != true || !link.Admin.Enabled)
        {
            return CollectStarted.No(
                "This station is switched off in ADL, so nothing under it is scanned or sent. "
                + "Nothing on this machine needs changing.");
        }

        if (string.IsNullOrWhiteSpace(link.Config.LocalFolderPath))
        {
            return CollectStarted.No(
                "No folder is bound to this station yet, so there is nowhere to collect from. "
                + "Set one under Edit settings first.");
        }

        lock (_gate)
        {
            if (_run is { Finished: false })
            {
                return CollectStarted.No(
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        "A collect is already running for {0}. Wait for it to finish.",
                        _run.StationName));
            }

            // Asked of the cycle rather than of the run above, because the
            // scheduled loop starts cycles this class never sees. The station
            // is named so the sentence is followable: it will be collected as
            // part of that cycle, and there is nothing else to do.
            if (_cycle.Running)
            {
                return CollectStarted.No(
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        "A cycle is already running on this machine — {0} will be collected as part of it.",
                        link.Admin.Station.Name));
            }

            var run = new Run(
                stationLinkId,
                link.Admin.Station.Name,
                connection.Name,
                link.Config.LocalFolderPath,
                link.Config.FilePattern ?? "",
                _time.GetUtcNow());

            _run = run;

            // Not awaited on purpose: see the note on this class. The task is
            // held by the run so nothing about it is unobserved.
            run.Started = Task.Run(() => RunAsync(run), CancellationToken.None);

            return CollectStarted.Yes(run.Progress());
        }
    }

    /// <summary>What the run in flight -- or the last one -- is doing.</summary>
    /// <remarks>
    /// The last one and not nothing, because the window asking is the window
    /// that has to show how it ended. A poll landing a moment after the final
    /// file would otherwise be told there was no run, on the screen somebody
    /// is watching for the answer.
    /// </remarks>
    public CollectProgress? Progress
    {
        get
        {
            lock (_gate)
            {
                return _run?.Progress();
            }
        }
    }

    /// <summary>
    /// Stop the run in flight, if it is the one being asked about.
    /// </summary>
    /// <remarks>
    /// A cancelled run leaves whatever it had already uploaded uploaded, and
    /// that is not a state to repair: the agent keeps no record of what it
    /// delivered, so the files it did not reach are offered again by the next
    /// cycle exactly as if this run had never happened.
    /// </remarks>
    public bool Cancel(long stationLinkId)
    {
        lock (_gate)
        {
            if (_run is not { Finished: false } run || run.StationLinkId != stationLinkId)
            {
                return false;
            }

            run.Cancelling.Cancel();

            return true;
        }
    }

    /// <summary>
    /// What this station's last requested collect came to, if that is still
    /// news.
    /// </summary>
    /// <param name="lastCycleAt">
    /// When the machine's last scheduled cycle finished, or null if none has.
    /// A requested collect older than that has been superseded by a cycle that
    /// covered the same station with fresher numbers, and the row should show
    /// the cycle instead -- so this answers null for it rather than leaving
    /// two ages of the same fact on one screen.
    /// </param>
    public RequestedCollect? For(long stationLinkId, DateTimeOffset? lastCycleAt)
    {
        if (!_results.TryGetValue(stationLinkId, out var result))
        {
            return null;
        }

        return lastCycleAt is null || result.At >= lastCycleAt ? result : null;
    }

    private async Task RunAsync(Run run)
    {
        try
        {
            var came = await _cycle
                .CollectStationAsync(run.StationLinkId, run, run.Cancelling.Token)
                .ConfigureAwait(false);

            run.Ended(came ?? new RequestedCollect
            {
                At = _time.GetUtcNow(),
                Error = "A cycle started on this machine before this one could.",
            });
        }
        catch (OperationCanceledException)
        {
            run.Ended(new RequestedCollect
            {
                At = _time.GetUtcNow(),
                Cancelled = true,
            });
        }
        catch (Exception exception)
        {
            // Nothing above this can catch anything: the task is started and
            // not awaited, and an exception escaping it would be an unobserved
            // one on a service that has to stay up.
            _logger.LogError(
                exception, "The requested collect for station link {Link} failed.", run.StationLinkId);

            run.Ended(new RequestedCollect
            {
                At = _time.GetUtcNow(),
                Error = exception.Message,
            });
        }

        _results[run.StationLinkId] = run.Result!;
    }

    /// <summary>
    /// One run: what it is for, where it has got to, and how to stop it.
    /// </summary>
    /// <remarks>
    /// Its own <see cref="ICollectWatcher"/>, so the cycle reports into the
    /// same object the poll reads out of and there is nothing to keep in step.
    /// </remarks>
    private sealed class Run(
        long stationLinkId,
        string stationName,
        string connectionName,
        string folder,
        string pattern,
        DateTimeOffset startedAt) : ICollectWatcher
    {
        private readonly Lock _gate = new();

        private LinkTally? _tally;
        private string _step = "Starting…";

        public long StationLinkId => stationLinkId;

        public string StationName => stationName;

        public CancellationTokenSource Cancelling { get; } = new();

        /// <summary>Held so the task is observed rather than dropped.</summary>
        public Task? Started { get; set; }

        public RequestedCollect? Result { get; private set; }

        public bool Finished => Result is not null;

        public void Step(string step)
        {
            lock (_gate)
            {
                _step = step;
            }
        }

        public void Counting(LinkTally? tally)
        {
            lock (_gate)
            {
                _tally = tally;
            }
        }

        public void Ended(RequestedCollect result)
        {
            lock (_gate)
            {
                Result = result;

                _step = result.Cancelled
                    ? "Stopped."
                    : result.Error is null ? "Finished." : "Stopped early.";
            }

            Cancelling.Dispose();
        }

        public CollectProgress Progress()
        {
            lock (_gate)
            {
                return new CollectProgress
                {
                    StationLinkId = stationLinkId,
                    StationName = stationName,
                    ConnectionName = connectionName,
                    LocalFolderPath = folder,
                    FilePattern = pattern,
                    Step = _step,
                    Running = Result is null,
                    Cancelled = Result?.Cancelled ?? false,
                    // The tally while there is one, so the numbers move during
                    // the delivery; the result afterwards, because the scan is
                    // gone by then and the result is what the row will show.
                    Scanned = Result?.Scanned ?? _tally?.Scanned ?? 0,
                    Offered = Result?.Offered ?? _tally?.Offered ?? 0,
                    Requested = _tally?.Requested ?? 0,
                    Uploaded = Result?.Uploaded ?? _tally?.Uploaded ?? 0,
                    Failed = Result?.Failed ?? _tally?.Failed ?? 0,
                    Error = Result?.Error ?? _tally?.Error,
                    StartedAt = startedAt,
                    FinishedAt = Result?.At,
                };
            }
        }
    }
}

/// <summary>
/// What came of asking for a collect: it is running, or here is why not.
/// </summary>
/// <remarks>
/// A result rather than an exception, because a refusal is a thing the window
/// draws rather than a thing that went wrong. A cycle already running is
/// ordinary Tuesday.
/// </remarks>
/// <param name="Progress">The run that is now under way, or null when none is.</param>
/// <param name="Refusal">The sentence to show instead, or null when there is none.</param>
public sealed record CollectStarted(CollectProgress? Progress, string? Refusal)
{
    public bool Ok => Progress is not null;

    public static CollectStarted Yes(CollectProgress progress) => new(progress, null);

    public static CollectStarted No(string refusal) => new(null, refusal);
}
