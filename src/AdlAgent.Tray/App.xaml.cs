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

    /// <summary>
    /// How a second tray asks the first one to show its window.
    /// </summary>
    /// <remarks>
    /// The mutex above stops a second icon appearing, and on its own that is
    /// all it does: the process that lost exits, nothing else happens, and a
    /// technician who double-clicked the desktop shortcut watches nothing
    /// happen at all. Which is the ordinary case, not the odd one -- the
    /// installer puts a shortcut in Startup, so by the time anybody reaches
    /// the desktop icon the tray it starts is already running.
    ///
    /// <c>Local\</c> for the mutex's reason: one tray per logon session, and
    /// a request from one session must not raise a window in another.
    /// </remarks>
    private const string ShowRequest = "Local\\adl-agent-tray-show";

    private Mutex? _theOnlyOne;
    private EventWaitHandle? _showRequested;
    private RegisteredWaitHandle? _showRequests;
    private BindingTrace? _bindings;
    private TrayPresence? _tray;
    private ShellViewModel? _shell;
    private MainWindow? _window;
    private DispatcherTimer? _timer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _theOnlyOne = new Mutex(initiallyOwned: true, OnlyInstance, out var isTheOnlyOne);

        // Created either way, and by both processes: whichever gets there
        // first makes it, the other opens the same one by name.
        _showRequested = new EventWaitHandle(
            initialState: false, EventResetMode.AutoReset, ShowRequest);

        if (!isTheOnlyOne)
        {
            // Quietly, but not silently: the window this process would have
            // opened already exists in the instance that owns the mutex, so
            // ask that one to put it in front of the person who just asked
            // for it, and go.
            _showRequested.Set();

            _showRequested.Dispose();
            _showRequested = null;
            _theOnlyOne.Dispose();
            _theOnlyOne = null;

            Shutdown();

            return;
        }

        // And this is the instance that answers them. The callback arrives on
        // a thread-pool thread, where touching a window is an exception, so
        // it does nothing but hand the request to the one thread that may.
        _showRequests = ThreadPool.RegisterWaitForSingleObject(
            _showRequested,
            (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow)),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        // Before the first window exists, so that the bindings evaluated as
        // it opens -- which is most of them -- are the ones this can report.
        _bindings = BindingTrace.StartIfAsked();

        // Both of the window's ways out of this process, built here rather
        // than found: the pipe to the service, and the request to Windows to
        // run the repoint verb elevated.
        _shell = new ShellViewModel(new AgentControlLink(), new ElevatedAddressChange());
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
        _bindings?.Dispose();

        // Before the handle it is waiting on.
        _showRequests?.Unregister(waitObject: null);
        _showRequested?.Dispose();

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

            // The colour is the line's, not a second opinion about the same
            // facts. Deciding it here as well is how a dot comes to sit amber
            // in the corner of a screen above a window saying there is
            // nothing to do.
            _tray.Show(_shell.NextStep.Attention, _shell.Headline);
        }
        catch (Exception exception)
        {
            _tray.Show(TrayState.Unknown, $"ADL Agent: {exception.Message}");
        }
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

        // Activate is a request, and Windows refuses it from a process that
        // is not the foreground one -- which this is not, when what asked was
        // a second copy of the tray started from a shortcut. Refused, it
        // flashes the taskbar button instead, which on a machine where the
        // window was never visible in the first place reads as nothing
        // happening. Topmost is not refused, and dropping it again in the
        // same breath leaves the window in front without leaving it stuck
        // over everything else.
        _window.Topmost = true;
        _window.Topmost = false;
    }
}
