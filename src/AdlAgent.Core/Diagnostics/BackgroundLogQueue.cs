using System.Threading.Channels;

namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// The one thread that touches a log's disk.
/// </summary>
/// <remarks>
/// The rule this class exists for is short: <em>no log call may perform disk
/// I/O on a cycle thread</em>. A cycle thread is uploading a country's
/// observations over a link that is measured in kilobits, and a folder on a
/// share that has just gone unresponsive would otherwise stop it for the
/// filesystem's timeout -- for a diagnostic. Writing is somebody else's
/// problem: the caller hands over a string and returns.
/// <para>
/// The queue is bounded, and full means dropped rather than blocked. A
/// blocking queue would put the hazard straight back where it was taken
/// from, on exactly the machine least able to afford it -- the one whose
/// disk has stopped answering. What is lost is said out loud when the writer
/// catches up, because a gap nobody is told about is worse than a gap.
/// </para>
/// </remarks>
public sealed class BackgroundLogQueue : IAsyncDisposable
{
    /// <summary>
    /// How many records may be waiting.
    /// </summary>
    /// <remarks>
    /// Generous enough that nothing an agent does in normal service can fill
    /// it -- a cycle writes one record per unit -- and small enough that a
    /// wedged disk costs a few megabytes of memory rather than all of it.
    /// </remarks>
    public const int Capacity = 4096;

    private readonly BoundedLogWriter _writer;
    private readonly Channel<Entry> _entries;
    private readonly Task _draining;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Func<long, string> _droppedNote;

    private long _dropped;

    /// <param name="droppedNote">
    /// How this log says that records were lost. Supplied by the caller
    /// because the two logs are read differently: one is JSON Lines a reader
    /// parses, and the other is text a person opens in Notepad.
    /// </param>
    public BackgroundLogQueue(BoundedLogWriter writer, Func<long, string> droppedNote)
    {
        _writer = writer;
        _droppedNote = droppedNote;
        _entries = Channel.CreateBounded<Entry>(new BoundedChannelOptions(Capacity)
        {
            // Wait, and then never wait: everything here writes with
            // TryWrite, which under this mode refuses rather than blocks when
            // the queue is full. It is the one setting that both keeps the
            // caller off the disk and lets the queue say what it lost --
            // DropWrite reports every write as accepted, so a queue using it
            // could neither count a drop nor be flushed.
            //
            // What is refused is the newest arrival. On the machine this
            // happens to, the first records of the flood are the ones that
            // say what started it.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        _draining = Task.Run(DrainAsync);
    }

    /// <summary>The log this queue writes into.</summary>
    public BoundedLogWriter Writer => _writer;

    /// <summary>How many records have been dropped for want of room.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Hand over one record. Never blocks and never throws.</summary>
    public void Write(string text)
    {
        if (!_entries.Writer.TryWrite(new Entry(text, null)))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>
    /// Wait until everything handed over so far has reached the disk.
    /// </summary>
    /// <remarks>
    /// For the two callers that genuinely need it: a test, and the
    /// diagnostics bundle, which is read off the same files it has just
    /// asked the agent to finish writing. Nothing on a cycle path calls it.
    /// </remarks>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        giveUp.CancelAfter(FlushGrace);

        try
        {
            var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // The queue may be full of the very records being waited for, so
            // the marker goes in as soon as the writer has taken one out.
            while (!_entries.Writer.TryWrite(new Entry(null, flushed)))
            {
                await Task.Delay(FlushPoll, giveUp.Token).ConfigureAwait(false);
            }

            await flushed.Task.WaitAsync(giveUp.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A disk that has stopped answering. The caller wanted the log
            // up to date and it is not; what it must not be is stuck, because
            // one of the two callers is a technician pressing a button.
        }
    }

    /// <summary>How long a flush waits before giving up on a disk.</summary>
    private static readonly TimeSpan FlushGrace = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan FlushPoll = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Stop taking records, write what is left, and let go of the files.
    /// </summary>
    /// <remarks>
    /// The remainder is written rather than abandoned: the records most worth
    /// keeping are often the last ones before a service was stopped, which is
    /// what somebody restarting a stuck machine is about to go looking for.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        _entries.Writer.TryComplete();

        try
        {
            await _draining.WaitAsync(ShutdownGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A disk that has stopped answering must not hold a service
            // shutdown open. What is in the queue is lost, which is the right
            // trade at this point.
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        _stopping.Dispose();
    }

    /// <summary>How long a shutdown waits for the queue to empty.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var entry in _entries.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                if (entry.Flushed is not null)
                {
                    entry.Flushed.TrySetResult();

                    continue;
                }

                Report();

                _writer.Write(entry.Text!);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // The drain is the only consumer, so an exception escaping it
            // would silently stop the log for the life of the process. There
            // is nowhere to report it to -- this is what reporting is -- so
            // the last thing it does is try to say so in the file itself.
            _writer.Write($"The log writer stopped: {exception}");
        }
    }

    /// <summary>
    /// Say what was lost, once per flood rather than once per record.
    /// </summary>
    private void Report()
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);

        if (dropped > 0)
        {
            _writer.Write(_droppedNote(dropped));
        }
    }

    /// <summary>A record to write, or somebody waiting for the ones before it.</summary>
    private readonly record struct Entry(string? Text, TaskCompletionSource? Flushed);
}
