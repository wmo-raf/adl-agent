using System;
using System.Windows;

namespace AdlAgent.Tray;

/// <summary>
/// One station, read rather than written.
/// </summary>
/// <remarks>
/// Modal, like the settings window and for the same reason: while it is open
/// the list behind it stops rebuilding, so this cannot end up describing a
/// station the grid no longer contains.
/// <para>
/// There is nothing in this file but the count that runs as the window
/// appears and the tidy-up on the way out. Everything that decides anything
/// is in <see cref="StationStatusViewModel"/>, where a test can reach it.
/// </para>
/// </remarks>
public partial class StationStatusWindow : Window
{
    private readonly StationStatusViewModel _status;

    public StationStatusWindow(StationStatusViewModel status)
    {
        _status = status;

        InitializeComponent();

        DataContext = status;
    }

    /// <summary>
    /// Count as the window appears, without being asked.
    /// </summary>
    /// <remarks>
    /// The question this window is opened to answer is almost always "is this
    /// binding still finding anything", and making somebody press a button
    /// for it would open the window on a blank rectangle where the answer
    /// goes. "Check again" is for the second and third times.
    /// </remarks>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _status.CheckCommand.Execute(null);
    }

    /// <summary>
    /// Open the machine's record, filtered to this station.
    /// </summary>
    /// <remarks>
    /// This window is modal and the passes window is not, which is a state
    /// worth being deliberate about: the passes window is owned by the main
    /// window rather than by this one, so closing this leaves it standing and
    /// gives the main window back. Opened on top rather than handed off,
    /// because the folder count above is what the technician came here for
    /// and closing this to answer a second question would throw it away.
    /// </remarks>
    private void ViewMore(object sender, RoutedEventArgs args) =>
        (Owner as MainWindow)?.ShowPasses(_status.StationLinkId);

    protected override void OnClosed(EventArgs e)
    {
        // Whatever happened, and however this window was closed, the list
        // behind it may move again.
        _status.Done();

        base.OnClosed(e);
    }
}
