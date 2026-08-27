namespace AdlAgent.Core.Cycle;

/// <summary>
/// How many files this machine may have on the wire at once, across every
/// unit it is collecting.
/// </summary>
/// <remarks>
/// One of these per tick, shared by every unit in it, which is the whole
/// point: the bound has to be the machine's rather than each unit's, or eight
/// units at four uploads apiece would be thirty-two sockets on a link chosen
/// for being the only one a country has.
/// <para>
/// Made fresh each tick rather than kept, because the number is ADL's and can
/// move between one sync and the next. A count that could not be resized
/// would have a machine keep last week's ceiling until somebody restarted the
/// service.
/// </para>
/// </remarks>
internal sealed class UploadSlots : IDisposable
{
    private readonly SemaphoreSlim _slots;

    public UploadSlots(int most)
    {
        Most = Math.Max(1, most);
        _slots = new SemaphoreSlim(Most, Most);
    }

    /// <summary>The ceiling, for whoever is deciding how wide to fan out.</summary>
    public int Most { get; }

    /// <summary>Wait for a slot, and give it back when the upload is done.</summary>
    public async Task<IDisposable> TakeAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new Slot(_slots);
    }

    public void Dispose() => _slots.Dispose();

    /// <summary>
    /// One upload's turn.
    /// </summary>
    /// <remarks>
    /// A struct returned as an interface rather than the semaphore released
    /// by hand, so that every path out of an upload -- taken, refused, thrown
    /// from, cancelled -- gives the slot back. A slot leaked on a failure
    /// path would narrow the machine one upload at a time until it stopped.
    /// </remarks>
    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;

        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
