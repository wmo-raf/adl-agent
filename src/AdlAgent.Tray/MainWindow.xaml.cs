using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

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

    /// <summary>The passes window, while one is open. Single-instance.</summary>
    private PassesWindow? _passes;

    public MainWindow(ShellViewModel shell)
    {
        _shell = shell;

        InitializeComponent();

        DataContext = shell;
    }

    /// <summary>
    /// Open this machine's ADL in whatever browser Windows opens addresses
    /// with.
    /// </summary>
    /// <remarks>
    /// <c>UseShellExecute</c> because the target is an address rather than a
    /// program: it is the shell that knows which browser this logon session
    /// has, and starting one by name would pick a browser for somebody.
    /// <para>
    /// Nothing about this can be allowed to end the process. A machine with
    /// no browser registered at all is an ordinary country server, and the
    /// answer to a click on it is a sentence at the bottom of the window
    /// giving the address to type somewhere else -- not an unhandled
    /// exception in the program that is supposed to explain what is wrong.
    /// </para>
    /// <para>
    /// The scheme is checked again here, having already been checked by
    /// <see cref="ShellViewModel.AdlLink"/> — which is what fills the
    /// <c>NavigateUri</c> this reads, so in this program the second check can
    /// never fire. It is here because of what is on the other side of it: a
    /// <c>file:</c> or <c>ms-settings:</c> reaching ShellExecute is a
    /// different kind of thing to hand the shell than the https link this row
    /// says it is, and that should not depend on a property three files away
    /// staying the way it is today.
    /// </para>
    /// </remarks>
    private void OpenAdl(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        var target = e.Uri;

        if (target is null
            || !target.IsAbsoluteUri
            || (target.Scheme != Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Deliberately every exception. Win32Exception is the one the
            // shell throws with no browser registered, but a locked-down
            // machine can refuse this half a dozen other ways and none of
            // them is worth telling apart: the technician's next move is the
            // same one, and it is on screen.
            _shell.BrowserRefused();
        }
    }

    /// <summary>
    /// Closing hides. The service is unaffected either way, and a technician
    /// who closed a window should not have to work out how to get the icon in
    /// the corner back.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;

        // The passes window goes with it. It is modeless and owned by this
        // one, so hiding the owner would otherwise leave a table of counts
        // standing on the desktop with no way back to the program it belongs
        // to -- and, being modeless, no reason for anybody to close it.
        _passes?.Close();

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

    /// <summary>
    /// Change where this machine reports, behind Windows' own consent.
    /// </summary>
    /// <remarks>
    /// The same shape as the station windows below, including the refresh
    /// afterwards: while the dialog is open the shell is not rebuilding rows
    /// at all, and a change that went through has just restarted the service
    /// under it. Everything the dialog decides is in
    /// <see cref="AdlAddressViewModel"/>; what is here is opening it.
    /// </remarks>
    private async void ChangeAdl(object sender, RoutedEventArgs args)
    {
        if (_shell.BeginChangingAdl() is not { } address)
        {
            return;
        }

        try
        {
            new AdlAddressWindow(address) { Owner = this }.ShowDialog();

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
            // opened, where leaving the flag set would freeze the station list
            // for as long as the tray runs.
            _shell.EndEditing();
        }
    }

    /// <summary>
    /// Write everything about this machine to a file somebody can email.
    /// </summary>
    /// <remarks>
    /// The dialog is here and the file is written by the service, which is the
    /// only arrangement that works on a properly installed machine: the logs
    /// live beside the device token, in a folder whose permissions the MSI has
    /// replaced with SYSTEM and Administrators, and this program runs as
    /// whoever is logged in. So the technician chooses where it goes and the
    /// service fills it.
    /// <para>
    /// A default name carrying the date, because the second thing that happens
    /// to this file is that it is attached to an email beside somebody else's.
    /// </para>
    /// </remarks>
    private async void SaveDiagnostics(object sender, RoutedEventArgs args)
    {
        try
        {
            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save ADL Agent diagnostics",
                FileName = $"adl-agent-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt",
                DefaultExt = ".txt",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                AddExtension = true,
            };

            if (save.ShowDialog(this) != true)
            {
                return;
            }

            await _shell.SaveDiagnosticsAsync(save.FileName).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // An async void handler: nothing above this can catch anything,
            // and an exception escaping would take down the one program on the
            // machine whose job is to explain what is wrong.
            _shell.Failed(exception);
        }
    }

    /// <summary>
    /// The machine's record of what it has collected, filtered to the
    /// selected station.
    /// </summary>
    private void StationPasses(object sender, RoutedEventArgs args)
    {
        if (_shell.SelectedStation is { } station)
        {
            ShowPasses(station.StationLinkId);
        }
    }

    /// <summary>The same window, over everything this machine has done.</summary>
    private void MachinePasses(object sender, RoutedEventArgs args) => ShowPasses(null);

    /// <summary>
    /// Open the passes window, or bring the open one forward.
    /// </summary>
    /// <remarks>
    /// One at a time. Two doors lead here and a third sits inside the Check
    /// status dialog, so without this a technician working through a problem
    /// would end up with three copies of the same table, each as stale as
    /// whenever it was opened.
    /// <para>
    /// Modeless, and the only window in this program that is: it holds no
    /// station row, so the reason the others freeze the list behind them does
    /// not apply, and being able to read it while pressing Collect now on the
    /// list behind is most of the point.
    /// </para>
    /// <para>
    /// Owned by this window rather than by whatever opened it, because one of
    /// the doors is inside a modal dialog and an owner that closed would take
    /// this with it.
    /// </para>
    /// </remarks>
    internal void ShowPasses(long? stationLinkId)
    {
        if (_passes is { IsLoaded: true })
        {
            // Pointed at what was asked for rather than merely raised.
            // Right-clicking a second station while the window is open would
            // otherwise show the first one's filter, focused and unchanged,
            // with nothing to say the one asked for had been dropped.
            _passes.FilterTo(stationLinkId);
            _passes.Activate();

            return;
        }

        _passes = new PassesWindow(_shell.Passes(stationLinkId)) { Owner = this };
        _passes.Closed += (_, _) => _passes = null;

        _passes.Show();
    }

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
