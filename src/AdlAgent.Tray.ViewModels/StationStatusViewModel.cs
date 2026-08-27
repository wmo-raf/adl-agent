using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;

namespace AdlAgent.Tray;

/// <summary>
/// One station, read rather than written: everything ADL holds about it, and
/// what its folder has in it right now.
/// </summary>
/// <remarks>
/// The grid can hold six columns and a station has three times that many
/// facts, so the ones a technician needs least often -- the WIGOS id, the
/// timezone the filenames are written in, the watermark ADL is asking from --
/// had nowhere to be. They were readable only by opening the settings window,
/// which is a window for changing things, over a station nobody wanted to
/// change.
/// <para>
/// The probe is the other half. Every other line here is a memory of the last
/// sync; the count is the only thing on the window that is true of this
/// machine at the moment somebody is looking at it, and "scanned 0, no error"
/// is answered by it and by nothing else. It runs the existing preview
/// command with no settings laid over the stored ones, so what is counted is
/// exactly the configuration the cycle will use.
/// </para>
/// <para>
/// The recent passes are the third thing, and they are the other half of that
/// same sentence. The probe can only ever speak about this instant; a station
/// that has been quietly doing nothing since Tuesday is a question about the
/// past, and until this list existed nothing on the machine could answer it --
/// the counts lived in memory and the pass after overwrote them.
/// </para>
/// <para>
/// Nothing here writes. That is what makes it safe to open on a station a
/// cycle is in the middle of, and it is why this is a separate class from
/// <see cref="StationSettingsViewModel"/> rather than a mode of it: a window
/// with no Save button has no dirty state, no refusal to render and no
/// question about what Cancel means.
/// </para>
/// </remarks>
public sealed class StationStatusViewModel : Observable
{
    private readonly ShellViewModel _shell;
    private bool _probing;

    public StationStatusViewModel(ShellViewModel shell, StationViewModel station)
    {
        _shell = shell;
        Station = station;

        CheckCommand = new AsyncCommand(CheckAsync, Failed, () => !_probing);
    }

    /// <summary>The station, as a copy: see <see cref="StationViewModel.Probing"/>.</summary>
    public StationViewModel Station { get; }

    /// <summary>Count the folder again, for somebody who has just fixed something.</summary>
    /// <remarks>
    /// Worth a button because the commonest use of this window is to have it
    /// open on one screen while a folder is being granted, a share remounted
    /// or a file dropped in on another. Without it the answer is only ever as
    /// fresh as the moment the window opened, and the way to get a new one is
    /// to close and reopen it.
    /// </remarks>
    public AsyncCommand CheckCommand { get; }

    /// <summary>
    /// The window's title: the station, and the connection it is under.
    /// </summary>
    /// <remarks>
    /// The same shape as the settings window's, and for the same reason. On a
    /// machine serving two vendors the station name alone does not say which
    /// set of folders these numbers are about.
    /// </remarks>
    public string Title => string.IsNullOrWhiteSpace(Station.ConnectionName)
        ? string.Create(CultureInfo.CurrentCulture, $"Station status — {Station.StationName}")
        : string.Create(
            CultureInfo.CurrentCulture,
            $"Station status — {Station.StationName}, under {Station.ConnectionName}");

    /// <summary>True while a count is in flight.</summary>
    public bool IsChecking => _probing;

    /// <summary>
    /// The last few passes this station has been in, newest first.
    /// </summary>
    /// <remarks>
    /// Headings only, and only three of them. This is the at-a-glance half of
    /// the question -- has anything happened here lately -- and the file
    /// detail, the filters and the whole machine's history are what
    /// <see cref="PassesViewModel"/> is for, one click away.
    /// </remarks>
    public ObservableCollection<PassRowViewModel> Passes { get; } = [];

    /// <summary>
    /// What to say where the passes go when there are none to show.
    /// </summary>
    /// <remarks>
    /// A sentence and not an empty box, because the empty box is the thing
    /// this whole feature exists to stop being the answer. A machine that has
    /// genuinely not collected yet and a service too old to keep a record are
    /// different states, and both of them look like nothing.
    /// </remarks>
    public string PassesMessage => _passesMessage;

    /// <summary>True when there is a list rather than a sentence.</summary>
    public bool HasPasses => Passes.Count > 0;

    /// <summary>
    /// True when there are older passes than the three shown.
    /// </summary>
    /// <remarks>
    /// What View more is offered on. Three rows with nothing to say they are
    /// three of many reads as a machine that has run three times.
    /// </remarks>
    public bool HasMorePasses => _more;

    /// <summary>The station this window's View more opens the table filtered to.</summary>
    public long StationLinkId => Station.StationLinkId;

    private string _passesMessage = "";
    private bool _more;

    /// <summary>
    /// Count the folder as it stands, and say so while it is happening.
    /// </summary>
    /// <remarks>
    /// The waiting is said out loud because it can be long: a preview walks a
    /// folder that may hold a hundred thousand files, and on a share it walks
    /// it over the network. A window that showed the previous count unchanged
    /// for eight seconds would be one a technician read the stale number off.
    /// </remarks>
    public async Task CheckAsync()
    {
        Probing(true);

        try
        {
            await _shell.CountMatchesAsync(Station).ConfigureAwait(true);
            await ReadPassesAsync().ConfigureAwait(true);
        }
        finally
        {
            Probing(false);
        }
    }

    /// <summary>
    /// Fill the recent-passes list from the machine's own record.
    /// </summary>
    /// <remarks>
    /// Beside the count rather than after a button of its own, because the two
    /// answer one question between them and a technician who has just fixed a
    /// share wants both re-read. It costs a local file read of a few hundred
    /// kilobytes, against a probe that may walk a folder over a network.
    /// </remarks>
    private async Task ReadPassesAsync()
    {
        var answer = await _shell.RecentPassesAsync(Station.StationLinkId).ConfigureAwait(true);

        Passes.Clear();

        foreach (var pass in answer.Passes)
        {
            Passes.Add(pass);
        }

        _more = answer.More;
        _passesMessage = answer.Problem
            ?? (Passes.Count > 0
                ? ""
                : "This machine has not recorded a collection pass for this station yet.");

        Raise(nameof(PassesMessage));
        Raise(nameof(HasPasses));
        Raise(nameof(HasMorePasses));
    }

    /// <summary>
    /// The window is closed, however it was closed; the rows may move again.
    /// </summary>
    /// <remarks>
    /// Called from the window's <c>OnClosed</c> rather than from any button,
    /// because the ways a window closes include the ones nobody wrote a
    /// handler for. Leaving it uncalled freezes the station list behind for as
    /// long as the tray runs.
    /// </remarks>
    public void Done() => _shell.EndEditing();

    private void Probing(bool running)
    {
        _probing = running;

        if (running)
        {
            Station.Say("Counting what is in this folder now…");
        }

        Raise(nameof(IsChecking));

        CheckCommand.Refresh();
    }

    private void Failed(Exception exception) =>
        Station.CouldNotCount($"Something went wrong in this window: {exception.Message}");
}
