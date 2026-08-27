using System;
using System.Windows;

namespace AdlAgent.Tray;

/// <summary>
/// This machine's record of what it has collected.
/// </summary>
/// <remarks>
/// Modeless, and the only window in this program that is. Every other one
/// holds a copy of a station row and freezes the list behind it so that it
/// cannot end up describing a station the list no longer contains; this one
/// holds no row, so that reason does not apply -- and the point of it is to
/// be readable while somebody presses Collect now on the list behind.
/// <para>
/// Owned by the main window rather than by whatever opened it. It can be
/// opened from inside the modal Check status window, and an owner that closed
/// would take this with it.
/// </para>
/// <para>
/// There is nothing in this file but the first read, the clipboard, and the
/// save dialog. Everything that decides anything is in
/// <see cref="PassesViewModel"/>, where a test can reach it.
/// </para>
/// </remarks>
public partial class PassesWindow : Window
{
    private readonly PassesViewModel _passes;

    public PassesWindow(PassesViewModel passes)
    {
        _passes = passes;

        InitializeComponent();

        DataContext = passes;
    }

    /// <summary>Point this window at a station, without opening a second one.</summary>
    internal void FilterTo(long? stationLinkId) => _passes.FilterTo(stationLinkId);

    /// <summary>
    /// Read as the window appears, without being asked.
    /// </summary>
    /// <remarks>
    /// The question this is opened to answer is always "what has this machine
    /// been doing", and making somebody press a button for it would open the
    /// window on an empty rectangle where the answer goes.
    /// </remarks>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _passes.RefreshCommand.Execute(null);
    }

    /// <summary>
    /// Read again when somebody comes back to this window.
    /// </summary>
    /// <remarks>
    /// This is the whole of the refresh policy, and there is deliberately no
    /// timer behind it. The control surface serves one client at a time and
    /// the tray already polls it every five seconds for the header; a second
    /// poller would contend for that one slot and make a working service
    /// report as absent. Coming back to the window is the moment somebody
    /// wants it current -- typically having just pressed Collect now behind
    /// it.
    /// </remarks>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // Only while the window is still showing the newest page. History does
        // not change, so a technician who has walked back through four pages
        // looking for something is reading a part of the log a refresh could
        // only throw away -- and closing the Save dialog is enough to raise
        // this.
        if (IsLoaded && _passes.RefreshOnReturn)
        {
            _passes.RefreshCommand.Execute(null);
        }
    }

    /// <summary>
    /// Put this pass on the clipboard as text.
    /// </summary>
    /// <remarks>
    /// Rendered by the same code that writes the diagnostics bundle, so the
    /// pass a technician pastes into an email and the pass HQ reads in the
    /// attachment are the same sentences. That property is worth keeping
    /// exactly here, where the text is about to leave the machine.
    /// </remarks>
    private void CopyPass(object sender, RoutedEventArgs args)
    {
        if (_passes.Detail is not { } detail)
        {
            return;
        }

        try
        {
            Clipboard.SetText(detail.Text);

            _passes.Say("That pass is on the clipboard.");
        }
        catch (Exception exception)
        {
            // The clipboard is a shared machine resource and another process
            // can be holding it open. Worth a line on the window and never a
            // dialog, and certainly not worth taking down the one program on
            // the machine that explains what is wrong.
            _passes.Say($"That pass could not be copied: {exception.Message}");
        }
    }

    /// <summary>
    /// Write a diagnostics bundle carrying what this window is showing.
    /// </summary>
    /// <remarks>
    /// The filter goes with it. A bundle that always carried the newest two
    /// hundred passes could not hold the failure three weeks back that this
    /// window had just been used to find -- which would make the window good
    /// at locating something it could not then report.
    /// </remarks>
    private async void SaveThese(object sender, RoutedEventArgs args)
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

            await _passes.SaveAsync(save.FileName).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // An async void handler: nothing above this can catch anything.
            _passes.Failed(exception);
        }
    }

    private void CloseWindow(object sender, RoutedEventArgs args) => Close();
}
