using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AdlAgent.Tray;

/// <summary>
/// The window behind the tray icon.
/// </summary>
/// <remarks>
/// Almost all of it is <c>MainWindow.xaml</c>. What is left here is closing
/// the window hiding it rather than ending the program, and the three ways a
/// station's settings are opened.
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>
    /// What the Browse button in the settings window decides, wired to the
    /// operating system this one is running on.
    /// </summary>
    /// <remarks>
    /// Built once and lent to each settings window rather than rebuilt per
    /// station: it holds no state about a station, only the two ways it asks
    /// Windows a question.
    /// </remarks>
    private readonly FolderChoice _folders =
        new(WindowsDriveMap.Lookup, Directory.Exists);

    private readonly ShellViewModel _shell;

    public MainWindow(ShellViewModel shell)
    {
        _shell = shell;

        InitializeComponent();

        DataContext = shell;
    }

    /// <summary>
    /// Closing hides. The service is unaffected either way, and a technician
    /// who closed a window should not have to work out how to get the icon in
    /// the corner back.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;

        Hide();

        base.OnClosing(e);
    }

    /// <summary>
    /// Enter and Right move from the connection list into the station grid.
    /// </summary>
    /// <remarks>
    /// Enter, because it means "go into this thing" one control to the right,
    /// and a pane where it is inert beside a grid where it opens teaches two
    /// rules for one key in one tab. Right, because that is the idiom every
    /// other master-detail list on this operating system uses.
    /// <para>
    /// Focus movement and nothing else: which station is now selected was
    /// decided when the connection was, by
    /// <see cref="ShellViewModel.SelectedConnection"/>. That is why this can
    /// live in a window without putting a decision somewhere the tests cannot
    /// reach it.
    /// </para>
    /// </remarks>
    private void ConnectionKeyed(object sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Enter or Key.Right))
        {
            return;
        }

        // Nothing to move into. Left unhandled so the key does whatever the
        // list would have done with it.
        if (StationGrid.Items.Count == 0)
        {
            return;
        }

        args.Handled = true;

        StationGrid.Focus();

        if (StationGrid.SelectedItem is { } selected)
        {
            StationGrid.CurrentCell = new DataGridCellInfo(selected, StationGrid.Columns[0]);
        }
    }

    /// <summary>
    /// Ask for the code box on a machine that is already paired.
    /// </summary>
    /// <remarks>
    /// The decision is the view model's — what the line and the box do about
    /// each other is <see cref="ShellViewModel.PairAgain"/>, where a test can
    /// reach it. This forwards a click, which is all this file is for.
    /// </remarks>
    private void PairAgain(object sender, RoutedEventArgs args) => _shell.PairAgain();

    /// <summary>Put the code box away again, unused.</summary>
    private void CancelPairAgain(object sender, RoutedEventArgs args) => _shell.CancelPairAgain();

    private void EditStation(object sender, RoutedEventArgs args) => OpenSettings();

    private void StationActivated(object sender, MouseButtonEventArgs args) => OpenSettings();

    /// <summary>
    /// The right button selects the row it landed on, before the menu over it
    /// opens.
    /// </summary>
    /// <remarks>
    /// WPF selects on the left button and not on the right, so without this a
    /// context menu would appear over the row under the pointer and act on
    /// whichever row was selected before -- opening one station's settings
    /// from another station's menu, which is a mistake nobody would see
    /// themselves make.
    /// <para>
    /// Deliberately not marked handled: the menu still has to open, and the
    /// grid still has to do whatever else it does with the press.
    /// </para>
    /// </remarks>
    private void StationRightClicked(object sender, MouseButtonEventArgs args)
    {
        if (sender is DataGridRow row)
        {
            row.IsSelected = true;
        }
    }

    /// <summary>
    /// Collect the selected station now, and watch it happen.
    /// </summary>
    /// <remarks>
    /// The run is started before the window opens, so a refusal -- a cycle
    /// already running, a station HQ switched off while this row was on
    /// screen -- is a sentence in the main window rather than a window that
    /// opens saying nothing is happening. Which is also why the rows are only
    /// frozen once a run is actually under way: see
    /// <see cref="ShellViewModel.BeginCollectingAsync"/>.
    /// </remarks>
    private async void CollectStation(object sender, RoutedEventArgs args)
    {
        try
        {
            if (await _shell.BeginCollectingAsync().ConfigureAwait(true) is not { } collect)
            {
                return;
            }

            try
            {
                new CollectWindow(collect) { Owner = this }.ShowDialog();
            }
            finally
            {
                // Whatever the window did, and whether or not it threw on the
                // way up. Leaving this unset would freeze the station list for
                // as long as the tray runs.
                _shell.EndEditing();
            }

            await _shell.RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // An async void handler: nothing above this can catch anything,
            // and an exception escaping would take down the one program on
            // the machine whose job is to explain what is wrong.
            _shell.Failed(exception);
        }
    }

    /// <summary>
    /// Open the selected station's status, and re-read everything once the
    /// window closes.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="OpenSettings"/>, including the refresh
    /// afterwards. Nothing in the status window writes, so there is nothing of
    /// its own for the refresh to pick up -- but the poll behind it has been
    /// suppressed for as long as it was open, and the list is that much out of
    /// date the moment it closes.
    /// </remarks>
    private async void CheckStation(object sender, RoutedEventArgs args)
    {
        if (_shell.BeginWatching() is not { } status)
        {
            return;
        }

        try
        {
            new StationStatusWindow(status) { Owner = this }.ShowDialog();

            await _shell.RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // An async void handler: nothing above this can catch anything.
            _shell.Failed(exception);

            // Idempotent, and the window's own OnClosed has usually done it
            // already. What this covers is a window that threw before it ever
            // opened, where leaving the flag set would freeze the station list
            // for as long as the tray runs.
            _shell.EndEditing();
        }
    }

    /// <summary>
    /// Enter opens the highlighted station.
    /// </summary>
    /// <remarks>
    /// Handled in preview and marked handled, because the grid's own answer
    /// to Enter is to move the highlight down a row -- which, on the key
    /// somebody presses to open the thing they have just selected, would open
    /// nothing and select something else.
    /// </remarks>
    private void StationKeyed(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || sender is not DataGrid)
        {
            return;
        }

        args.Handled = true;

        OpenSettings();
    }

    /// <summary>
    /// Open the selected station's settings, and re-read everything once the
    /// window closes.
    /// </summary>
    /// <remarks>
    /// The refresh is after the dialog rather than inside the save, and it
    /// runs whatever the save came to -- or whether there was one. While the
    /// window is open the shell is not rebuilding rows at all (see
    /// <see cref="ShellViewModel.BeginEditing"/>), so this is the moment the
    /// list catches up, and it catches up with what ADL holds rather than with
    /// what somebody typed.
    /// </remarks>
    private async void OpenSettings()
    {
        if (_shell.BeginEditing(_folders) is not { } settings)
        {
            return;
        }

        try
        {
            new StationSettingsWindow(settings, _folders) { Owner = this }.ShowDialog();

            await _shell.RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // An async void handler: nothing above this can catch anything,
            // and an exception escaping would take down the one program on
            // the machine whose job is to explain what is wrong.
            _shell.Failed(exception);

            // Idempotent, and the window's own OnClosed has usually done it
            // already. What this covers is a window that threw before it ever
            // opened -- where leaving the flag set would freeze the station
            // list for as long as the tray runs.
            _shell.EndEditing();
        }
    }
}
