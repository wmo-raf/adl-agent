using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace AdlAgent.Tray;

/// <summary>
/// The window behind the tray icon.
/// </summary>
/// <remarks>
/// Almost all of it is <c>MainWindow.xaml</c>. What is left here is the two
/// behaviours that are not layout: closing the window hides it rather than
/// ending the program, and typing in a settings box re-counts the files it
/// would match after a short pause.
/// </remarks>
public partial class MainWindow : Window
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

    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _settling;

    public MainWindow(ShellViewModel shell)
    {
        _shell = shell;

        InitializeComponent();

        DataContext = shell;

        _settling = new DispatcherTimer { Interval = Settle };
        _settling.Tick += Recount;

        shell.StationSettingsChanged += Restart;
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
            await _shell.CountMatchesAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // A timer tick cannot be awaited by anyone, so an exception
            // escaping here would close the window a technician is in the
            // middle of using. The count is the least important thing on it.
            _shell.SelectedStation?.CouldNotCount(exception.Message);
        }
    }
}
