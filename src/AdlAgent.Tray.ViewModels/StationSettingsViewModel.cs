using System;
using System.Globalization;
using System.Threading.Tasks;

namespace AdlAgent.Tray;

/// <summary>
/// One editing session: the station being typed into, what to say about it,
/// and the one button that goes to ADL.
/// </summary>
/// <remarks>
/// This exists because the settings window is modal and the main window is
/// not. While it is up, the station in <see cref="Station"/> is the only
/// station anything is being done to -- so the shell can stop rebuilding its
/// rows (see <see cref="ShellViewModel.BeginEditing"/>), and nothing here has
/// to keep a selection and an editor in step.
/// <para>
/// It holds no settings of its own. <see cref="Station"/> is a copy the shell
/// made from the station snapshot, and dropping this object is what Cancel
/// means.
/// </para>
/// </remarks>
public sealed class StationSettingsViewModel : Observable
{
    private readonly ShellViewModel _shell;

    public StationSettingsViewModel(ShellViewModel shell, StationViewModel station)
    {
        _shell = shell;
        Station = station;

        SaveCommand = new AsyncCommand(() => SaveAsync(), Failed, () => Station.HasChanges);

        Station.SettingsChanged += Edited;
    }

    /// <summary>The boxes. A copy: see <see cref="StationViewModel.Editing"/>.</summary>
    public StationViewModel Station { get; }

    public AsyncCommand SaveCommand { get; }

    /// <summary>
    /// Raised once a save has been answered, with what it came to.
    /// </summary>
    /// <remarks>
    /// An event rather than a return value the window awaits, so the button
    /// can stay an <see cref="AsyncCommand"/> -- which is what stops a
    /// technician pressing Save four times while the first press is still
    /// travelling.
    /// </remarks>
    public event EventHandler<SaveOutcome>? Saved;

    /// <summary>Raised when a box changes, so the window can re-count.</summary>
    public event EventHandler? SettingsChanged;

    public string Title => string.Create(
        CultureInfo.CurrentCulture,
        $"Station settings — {Station.StationName}");

    public bool HasChanges => Station.HasChanges;

    /// <summary>
    /// The line along the bottom of the window: what happened, or why the
    /// Save button beside it is grey.
    /// </summary>
    /// <remarks>
    /// A disabled button with no stated reason is a button somebody stares
    /// at. The three states are the whole of it -- ADL has said something,
    /// nothing has been typed yet, or something has been typed and not sent
    /// -- and only the middle one needs a sentence written for it.
    /// <para>
    /// The message itself is the shell's, not a second copy. A refusal is
    /// read here because this window is in front of the other one; a success
    /// is read in the main window because by then this one has closed. One
    /// string either way, so the two cannot come to disagree.
    /// </para>
    /// </remarks>
    public string Answer => _shell.Message.Length > 0
        ? _shell.Message
        : Station.HasChanges ? "" : "Nothing has changed yet.";

    /// <summary>Send the boxes that differ, and say what became of them.</summary>
    public async Task<SaveOutcome> SaveAsync()
    {
        var outcome = await _shell.SaveStationAsync(Station).ConfigureAwait(true);

        Raise(nameof(Answer));

        Saved?.Invoke(this, outcome);

        return outcome;
    }

    /// <summary>Count what these boxes would match, against this machine.</summary>
    public Task CountAsync() => _shell.CountMatchesAsync(Station);

    /// <summary>Say what could not be counted, when the count itself threw.</summary>
    public void CouldNotCount(string detail) => Station.CouldNotCount(detail);

    /// <summary>
    /// The editing session is over, however it ended.
    /// </summary>
    /// <remarks>
    /// Called from the window's <c>OnClosed</c> rather than from Save or
    /// Cancel, because the ways a window closes include the ones nobody
    /// wrote a handler for. Leaving it uncalled would leave the station list
    /// behind frozen for as long as the tray runs.
    /// </remarks>
    public void Done()
    {
        Station.SettingsChanged -= Edited;

        _shell.EndEditing();
    }

    private void Edited(object? sender, EventArgs args)
    {
        // Whatever ADL last said was about the boxes as they were, and they
        // are not those any more. Cleared rather than left to go stale under
        // a technician who is already fixing the thing it complained about.
        _shell.Message = "";

        SaveCommand.Refresh();

        Raise(nameof(HasChanges));
        Raise(nameof(Answer));

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Failed(Exception exception)
    {
        _shell.Message = $"Something went wrong in this window: {exception.Message}";

        Raise(nameof(Answer));
    }
}
