using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AdlAgent.Tray;

/// <summary>
/// Where one station's folder and file pattern are set.
/// </summary>
/// <remarks>
/// Modal, and one at a time. That is what lets the list behind it stop moving
/// while this is open, and it is why nothing here has to keep a row and an
/// editor in step: the station in <see cref="StationSettingsViewModel.Station"/>
/// is a copy, and closing without saving simply drops it.
/// <para>
/// What is in this file is the three things that are not decisions: the
/// folder dialog, the debounce that re-counts after somebody stops typing,
/// and the confirmation before throwing away unsaved boxes. Everything that
/// decides anything -- what a picked path becomes, what to warn about, and
/// whether a save means this window is finished -- is next door in
/// <c>AdlAgent.Tray.ViewModels</c>, where a test can reach it.
/// </para>
/// </remarks>
public partial class StationSettingsWindow : Window
{
    /// <summary>
    /// How long to wait after the last keystroke before counting.
    /// </summary>
    /// <remarks>
    /// Each count walks a folder, and the folders this product exists for
    /// hold hundreds of thousands of files. Counting on every keystroke would
    /// have the agent walk one such folder eleven times while somebody typed
    /// "GARISSA_*.dat" -- and the answers would arrive out of order. Long
    /// enough to mean "they have stopped typing", short enough to still feel
    /// like it is answering them.
    /// </remarks>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    private readonly StationSettingsViewModel _settings;
    private readonly FolderChoice _folders;
    private readonly DispatcherTimer _settling;

    /// <summary>True once ADL has taken these settings, or refused them for good.</summary>
    private bool _finished;

    public StationSettingsWindow(StationSettingsViewModel settings, FolderChoice folders)
    {
        _settings = settings;
        _folders = folders;

        InitializeComponent();

        DataContext = settings;

        _settling = new DispatcherTimer { Interval = Settle };
        _settling.Tick += Recount;

        settings.SettingsChanged += Restart;
        settings.Saved += Finished;
    }

    /// <summary>
    /// Count once as the window appears, before anything has been typed.
    /// </summary>
    /// <remarks>
    /// A technician opening a station's settings usually wants to know
    /// whether the folder ADL already holds is finding anything -- that is
    /// frequently the whole reason they opened it. Waiting for a keystroke to
    /// answer a question nobody has to ask twice would open this window on a
    /// blank rectangle where the answer goes.
    /// </remarks>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        Recount(this, EventArgs.Empty);
    }

    /// <summary>
    /// Closing throws away whatever has not been saved, and says so first.
    /// </summary>
    /// <remarks>
    /// Here rather than on the Cancel button, because the ways a window
    /// closes are more numerous than the buttons on it: Escape, the titlebar,
    /// and the task bar all arrive here too, and a technician who loses a
    /// folder path they typed should have been asked by all of them.
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished && _settings.HasChanges && !Discard())
        {
            e.Cancel = true;

            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _settling.Stop();
        _settling.Tick -= Recount;

        _settings.SettingsChanged -= Restart;
        _settings.Saved -= Finished;

        // Whatever happened, the list behind this window may move again.
        _settings.Done();

        base.OnClosed(e);
    }

    /// <summary>
    /// A save that ADL answered. Everything except a refusal is this window
    /// being finished -- a refusal is the one answer whose subject is still
    /// on the screen.
    /// </summary>
    private void Finished(object? sender, SaveOutcome outcome)
    {
        if (outcome == SaveOutcome.Refused)
        {
            return;
        }

        _finished = true;

        Close();
    }

    private void Browse(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Where this station's files are written",
            Multiselect = false,
        };

        if (_folders.StartingFolder(_settings.Station.LocalFolderPath) is { } starting)
        {
            dialog.InitialDirectory = starting;
        }

        if (dialog.ShowDialog(this) == true)
        {
            // Through Accept rather than straight onto the box: a letter
            // mapped in this technician's session is a path the service can
            // never see, and the share behind it is one it might.
            _settings.Station.LocalFolderPath = _folders.Accept(dialog.FolderName);
        }
    }

    private bool Discard() =>
        MessageBox.Show(
            this,
            "These settings have not been saved to ADL. Close the window and lose them?",
            "Station settings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Restart(object? sender, EventArgs args)
    {
        // Restarted rather than left running, so the clock measures the pause
        // since the last keystroke rather than since the first.
        _settling.Stop();
        _settling.Start();
    }

    private async void Recount(object? sender, EventArgs args)
    {
        _settling.Stop();

        try
        {
            await _settings.CountAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // A timer tick cannot be awaited by anyone, so an exception
            // escaping here would close the window a technician is in the
            // middle of using. The count is the least important thing on it.
            _settings.CouldNotCount(exception.Message);
        }
    }
}
