using AdlAgent.Core.Hosting;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The nudge that cuts a loop's sleep short, and the moment it exists for.
/// </summary>
/// <remarks>
/// A technician types the pairing code into the tray and watches for the
/// machine to appear in the fleet view. The loops sleep for minutes at a
/// time, and sleeping through that one moment is what makes a working install
/// look like a broken one.
/// </remarks>
public class WakeSignalTests
{
    // Long enough that a signal that failed to arrive is a hang rather than
    // a slow pass, so the test says which of the two happened.
    private static readonly TimeSpan ALongSleep = TimeSpan.FromHours(1);

    [Fact]
    public async Task A_wake_arriving_while_a_loop_works_is_not_lost()
    {
        var signal = new AgentWakeSignal();

        // The loop starts listening, then does its pass. This is the order
        // that matters: Set() completes whatever is pending and puts a fresh
        // one in its place, so a loop that reached for the pending task only
        // after its pass would reach for the replacement -- and sleep through
        // the signal for a whole check interval.
        var listening = signal.Listen();

        signal.Set();

        var woken = await signal.WaitAsync(
            listening, ALongSleep, TimeProvider.System, CancellationToken.None);

        Assert.True(woken);
    }

    [Fact]
    public async Task A_wake_reaches_every_loop_that_was_listening()
    {
        var signal = new AgentWakeSignal();

        // Three loops -- heartbeat, scan cycle, update check -- and the
        // pairing that wakes them is meant for all three.
        var listeners = new[] { signal.Listen(), signal.Listen(), signal.Listen() };

        signal.Set();

        var woken = await Task.WhenAll(listeners.Select(listening =>
            signal.WaitAsync(listening, ALongSleep, TimeProvider.System, CancellationToken.None)));

        Assert.All(woken, Assert.True);
    }

    [Fact]
    public async Task A_loop_that_starts_listening_after_a_wake_sleeps_through_it()
    {
        var signal = new AgentWakeSignal();

        signal.Set();

        // Not a missed wake but a finished one: whatever it was for has
        // already been done by the loops that were listening at the time.
        // Honouring it here would have a loop run twice for one nudge, and on
        // a machine on a metered link that is a round trip nobody asked for.
        var listening = signal.Listen();

        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            signal.WaitAsync(listening, ALongSleep, TimeProvider.System, giveUp.Token));
    }
}
