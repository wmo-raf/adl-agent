namespace AdlAgent.Core.Hosting;

/// <summary>
/// A nudge that cuts short whatever a loop is waiting for.
/// </summary>
/// <remarks>
/// It exists for one moment: a technician has just typed a pairing code into
/// the tray, and is watching to see the machine appear in the fleet view. The
/// loops are asleep for minutes at a time and there is nothing wrong with
/// that, but making them sleep through the one moment somebody is watching
/// would make a working install look like a broken one.
/// <para>
/// A signal rather than a shorter idle poll, deliberately: an idle cadence
/// would be a second cadence to reason about, and these machines are on
/// metered links.
/// </para>
/// </remarks>
public sealed class AgentWakeSignal
{
    private readonly Lock _gate = new();

    private TaskCompletionSource _pending = NewSource();
    private int _waiting;

    /// <summary>
    /// How many loops are currently asleep on this signal.
    /// </summary>
    /// <remarks>
    /// A diagnostic, and the one thing that makes the cadence testable
    /// without guessing: a test driving a fake clock has to know the loops
    /// have reached their timers before it moves time, or it is asserting on
    /// a race rather than on a cadence.
    /// </remarks>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>Wake every loop that is currently waiting.</summary>
    public void Set()
    {
        TaskCompletionSource waiting;

        lock (_gate)
        {
            waiting = _pending;
            _pending = NewSource();
        }

        waiting.TrySetResult();
    }

    /// <summary>
    /// Wait for <paramref name="delay"/>, or until someone calls
    /// <see cref="Set"/>.
    /// </summary>
    /// <returns>True when woken early.</returns>
    public async Task<bool> WaitAsync(
        TimeSpan delay, TimeProvider time, CancellationToken cancellationToken)
    {
        Task woken;

        lock (_gate)
        {
            woken = _pending.Task;
        }

        // The timer is cancelled on the way out, not abandoned. A loop woken
        // early would otherwise leave a live timer behind for the rest of
        // the interval, and this runs for months: one orphan per pairing is
        // nothing, one per wake-up on a chatty tray is a leak.
        using var timing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var elapsed = Task.Delay(delay, time, timing.Token);

        Interlocked.Increment(ref _waiting);

        try
        {
            var finished = await Task.WhenAny(woken, elapsed).ConfigureAwait(false);

            if (finished == woken)
            {
                return true;
            }

            // Awaited rather than dropped so that a cancelled wait throws
            // here instead of quietly reading as "the interval passed".
            await elapsed.ConfigureAwait(false);

            return false;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
