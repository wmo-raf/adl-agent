using System;
using System.Globalization;
using System.Threading.Tasks;
using AdlAgent.Core.Cycle;
using AdlAgent.Windows.Platform;

namespace AdlAgent.Tray;

/// <summary>
/// One collect asked for at the machine, while somebody watches it.
/// </summary>
/// <remarks>
/// It polls rather than being told, and that is the control surface's
/// constraint rather than a preference: the pipe serves one client at a time,
/// so a run that reported itself down a held connection would freeze the
/// tray's own status poll -- and with it the header, the next-step line and
/// the colour of the icon in the corner -- for the length of an upload. Short
/// questions, short answers, and everything else on the screen goes on
/// moving.
/// <para>
/// The run belongs to the service and not to this object. Closing the window
/// stops watching; it does not stop the run, and it cannot, because a run
/// this window forgot about is still one the service is in the middle of.
/// Cancel is the thing that stops it, and it is a command like any other.
/// </para>
/// </remarks>
public sealed class CollectViewModel : Observable
{
    private readonly AgentControlLink _agent;

    private CollectProgress _progress;

    public CollectViewModel(AgentControlLink agent, CollectProgress started)
    {
        _agent = agent;
        _progress = started;

        CancelCommand = new AsyncCommand(CancelAsync, Failed, () => Running);
    }

    /// <summary>Stop the run. Grey once it has stopped by itself.</summary>
    public AsyncCommand CancelCommand { get; }

    /// <summary>Raised once the run has stopped, however it stopped.</summary>
    /// <remarks>
    /// So the window can stop its timer. Nothing closes on it: a technician
    /// pressed this to find out what happened, and a window that vanished at
    /// the moment the answer arrived would be one that never showed it.
    /// </remarks>
    public event EventHandler? Finished;

    public long StationLinkId => _progress.StationLinkId;

    /// <summary>The window's title: the station, and the connection it is under.</summary>
    public string Title => string.Create(
        CultureInfo.CurrentCulture, $"Collecting — {_progress.StationName}");

    /// <summary>The station and the link id, under the title.</summary>
    public string Station => string.IsNullOrWhiteSpace(_progress.ConnectionName)
        ? string.Create(CultureInfo.CurrentCulture, $"link {_progress.StationLinkId}")
        : string.Create(
            CultureInfo.CurrentCulture,
            $"{_progress.ConnectionName} · link {_progress.StationLinkId}");

    /// <summary>What is being walked, so the window says what it is working on.</summary>
    public string Binding => string.Create(
        CultureInfo.CurrentCulture, $"{_progress.LocalFolderPath}  {_progress.FilePattern}");

    /// <summary>Which part of the cycle this now is, in a sentence.</summary>
    public string Step => _progress.Step;

    /// <summary>The counts, as they move.</summary>
    public string Counts => string.Create(
        CultureInfo.CurrentCulture,
        $"{_progress.Scanned} seen · {_progress.Offered} offered · {_progress.Uploaded} sent");

    public string Failures => string.Create(
        CultureInfo.CurrentCulture, $"{_progress.Failed} failed");

    /// <summary>True while the run is still going.</summary>
    public bool Running => _progress.Running;

    /// <summary>What went wrong, or nothing.</summary>
    public string Problem => _progress.Error ?? "";

    public bool HasProblem => Problem.Length > 0;

    /// <summary>
    /// Ask the service where the run has got to.
    /// </summary>
    /// <remarks>
    /// A poll that cannot reach the service is not a failed run. The commonest
    /// reason for one is the surface having let go of its pipe between two
    /// clients, which is why the link tries twice before saying so at all --
    /// and a window that declared the collect dead over it would be lying
    /// about a run that is still uploading.
    /// </remarks>
    public async Task PollAsync()
    {
        if (!Running)
        {
            return;
        }

        var progress = await _agent.CollectStatusAsync().ConfigureAwait(true);

        if (progress.Value is null)
        {
            return;
        }

        Show(progress.Value);
    }

    /// <summary>Stop the run, and show what it had done when it stopped.</summary>
    public async Task CancelAsync()
    {
        var stopped = await _agent.CancelCollectAsync(StationLinkId).ConfigureAwait(true);

        if (stopped.Value is null)
        {
            // It finished between the button being drawn and being pressed.
            // The next poll says so, and there is nothing to report.
            return;
        }

        Show(stopped.Value);
    }

    private void Show(CollectProgress progress)
    {
        // The service answers collect_status with whatever run it last had,
        // and after a cancel the scheduled loop may have started another. A
        // window showing a different station's numbers under this station's
        // title would be worse than one that had stopped updating.
        if (progress.StationLinkId != StationLinkId)
        {
            return;
        }

        var wasRunning = _progress.Running;

        _progress = progress;

        foreach (var property in Everything)
        {
            Raise(property);
        }

        CancelCommand.Refresh();

        if (wasRunning && !progress.Running)
        {
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Failed(Exception exception)
    {
        _progress = _progress with
        {
            Error = $"Something went wrong in this window: {exception.Message}",
        };

        Raise(nameof(Problem));
        Raise(nameof(HasProblem));
    }

    /// <summary>
    /// Everything drawn from the progress answer.
    /// </summary>
    /// <remarks>
    /// Listed once and raised together, because they are all views of one
    /// record and change together. Raising them as each was noticed is how a
    /// window comes to show "Finished." above counts from the poll before.
    /// </remarks>
    private static readonly string[] Everything =
    [
        nameof(Title), nameof(Station), nameof(Binding), nameof(Step),
        nameof(Counts), nameof(Failures), nameof(Running),
        nameof(Problem), nameof(HasProblem),
    ];
}
