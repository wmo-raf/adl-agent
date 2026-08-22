using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AdlAgent.Windows.Platform;

namespace AdlAgent.Tray;

/// <summary>
/// The tray program: an icon, a window behind it, and a timer that keeps both
/// in step with the service.
/// </summary>
/// <remarks>
/// It owns no state about the agent. The poll below is the only thing that
/// happens on its own, and all it does is ask the service the same question
/// the window asks when a technician presses Refresh.
/// </remarks>
public partial class App : Application
{
    /// <summary>
    /// How often the icon and the window re-read the service.
    /// </summary>
    /// <remarks>
    /// Chosen against the two things it is showing rather than against the
    /// cost, which is a local pipe call and a few dozen station rows. The
    /// heartbeat is every five minutes and the scan cycle every ten, so
    /// nothing here moves faster than this; five seconds is short enough that
    /// a technician who has just pressed Pair, or just started the service,
    /// sees the window agree with them before they wonder whether it is
    /// stuck.
    /// </remarks>
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The name the one tray per logon session is held under.
    /// </summary>
    /// <remarks>
    /// <c>Local\</c>, so it is per session rather than machine-wide: a
    /// server with two administrators on it over RDP should get one icon
    /// each, not one between them. What it stops is the commoner mistake --
    /// the same person starting the tray twice and getting two icons, two
    /// polls, and two clients contending for a control surface that serves
    /// one at a time.
    /// </remarks>
    private const string OnlyInstance = "Local\\adl-agent-tray";

    private Mutex? _theOnlyOne;
    private TrayPresence? _tray;
    private ShellViewModel? _shell;
    private MainWindow? _window;
    private DispatcherTimer? _timer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _theOnlyOne = new Mutex(initiallyOwned: true, OnlyInstance, out var isTheOnlyOne);

        if (!isTheOnlyOne)
        {
            // Quietly: a technician who double-clicked twice wanted one
            // window, and the one they already have is about to be in front
            // of them anyway.
            _theOnlyOne.Dispose();
            _theOnlyOne = null;

            Shutdown();

            return;
        }

        _shell = new ShellViewModel(new AgentControlLink());
        _tray = new TrayPresence();
        _window = new MainWindow(_shell);

        _tray.Opened += (_, _) => ShowWindow();
        _tray.Closed += (_, _) => Shutdown();

        _timer = new DispatcherTimer { Interval = Poll };
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        _timer.Start();

        // The first read happens now rather than in five seconds: a
        // technician who has just started this expects the icon to mean
        // something immediately.
        _ = RefreshAsync();

        // A machine that is not paired yet has exactly one thing to do, and
        // nobody starting this program for the first time should have to
        // discover that the icon in the corner is clickable.
        ShowWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        _tray?.Dispose();

        if (_theOnlyOne is not null)
        {
            _theOnlyOne.ReleaseMutex();
            _theOnlyOne.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Ask the service, and repaint. Never throws.
    /// </summary>
    /// <remarks>
    /// The caller is a timer tick, which cannot await anything and cannot
    /// catch anything: an exception escaping here would take the process
    /// down with no window and no message, on the machine where this program
    /// is the thing that is supposed to explain what is wrong.
    /// </remarks>
    private async Task RefreshAsync()
    {
        if (_shell is null || _tray is null)
        {
            return;
        }

        try
        {
            await _shell.RefreshAsync().ConfigureAwait(true);

            _tray.Show(StateOf(_shell), _shell.Headline);
        }
        catch (Exception exception)
        {
            _tray.Show(TrayState.Unknown, $"ADL Agent: {exception.Message}");
        }
    }

    /// <summary>The colour of the dot, derived from what the shell was told.</summary>
    private static TrayState StateOf(ShellViewModel shell)
    {
        if (!shell.ServiceRunning)
        {
            return TrayState.Stopped;
        }

        if (shell.NeedsRePairing || !shell.IsPaired || shell.HasAlert)
        {
            return TrayState.NeedsAttention;
        }

        return TrayState.Working;
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }
}
