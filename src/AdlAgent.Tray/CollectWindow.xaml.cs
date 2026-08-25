using System;
using System.Windows;
using System.Windows.Threading;

namespace AdlAgent.Tray;

/// <summary>
/// One station being collected now, watched while it happens.
/// </summary>
/// <remarks>
/// The whole of this file is a timer. The control surface serves one client
/// at a time, so the run reports itself in answers to short questions rather
/// than down a held connection -- and asking those questions on a cadence is
/// the window's job, not a decision, which is why everything that decides
/// anything is in <see cref="CollectViewModel"/> where a test can reach it.
/// </remarks>
public partial class CollectWindow : Window
{
    /// <summary>
    /// How often to ask where the run has got to.
    /// </summary>
    /// <remarks>
    /// A second, because that is about as slowly as a number can move and
    /// still read as live to somebody watching it, and about as often as this
    /// is worth asking: each poll is a fresh pipe conversation, and the tray's
    /// own status poll is going down the same one-client-at-a-time surface
    /// every five.
    /// </remarks>
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

    private readonly CollectViewModel _collect;
    private readonly DispatcherTimer _polling;

    public CollectWindow(CollectViewModel collect)
    {
        _collect = collect;

        InitializeComponent();

        DataContext = collect;

        _polling = new DispatcherTimer { Interval = Cadence };
        _polling.Tick += Poll;

        collect.Finished += Stopped;

        _polling.Start();
    }

    /// <summary>
    /// Stop asking once the run has stopped.
    /// </summary>
    /// <remarks>
    /// The window stays open. A technician pressed the item to find out what
    /// would happen, and a window that vanished at the moment the answer
    /// arrived would be one that never showed it.
    /// </remarks>
    private void Stopped(object? sender, EventArgs args) => _polling.Stop();

    private void CloseWindow(object sender, RoutedEventArgs args) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _polling.Stop();
        _polling.Tick -= Poll;

        _collect.Finished -= Stopped;

        base.OnClosed(e);
    }

    private async void Poll(object? sender, EventArgs args)
    {
        try
        {
            await _collect.PollAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // A timer tick cannot be awaited by anyone, so an exception
            // escaping here would close the window somebody is watching a
            // collect through -- and the collect would go on regardless.
            // Stopping the polling is the honest response: the numbers freeze
            // where they were rather than the window disappearing.
            _polling.Stop();
        }
    }
}
