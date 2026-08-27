using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AdlAgent.Core.Diagnostics;
using AdlAgent.Windows.Platform;

namespace AdlAgent.Tray;

/// <summary>
/// Everything this machine has recorded about its own collection, as a table.
/// </summary>
/// <remarks>
/// Not a tab. <see cref="TrayTabs"/> records that the Pairing tab was deleted
/// precisely because it gave a technician a whole screen with nothing on it
/// to do, in the leftmost and most prominent position, and a machine-wide
/// activity tab would be that again on the many machines that are simply
/// working. A window opened on demand is a different thing: it costs nothing
/// until somebody has a question.
/// <para>
/// Modeless, unlike every other window the tray opens, and for a reason that
/// is the mirror of theirs. Those hold a copy of a station row and freeze the
/// list behind them so they cannot end up describing a station that has gone;
/// this holds no station row, so the reason does not apply -- and a
/// technician wants to press Collect now on the list behind and then look
/// here for what it did.
/// </para>
/// </remarks>
public sealed class PassesViewModel : Observable
{
    /// <summary>
    /// The most rows this window will hold, however often Load more is
    /// pressed.
    /// </summary>
    /// <remarks>
    /// A stated bound rather than an unbounded table, because every other
    /// bound in this record is stated and because the alternative is a tray
    /// process on a ministry's server holding a hundred thousand rows nobody
    /// is reading the middle of. Past it the filter is the way further back,
    /// which is the motion that was going to find the thing anyway.
    /// </remarks>
    public const int Ceiling = 5_000;

    /// <summary>How many rows one request asks for.</summary>
    /// <remarks>
    /// Comfortably inside what one control message carries, so that a page is
    /// almost never trimmed on the way over -- and generously more than a
    /// screen, so that scrolling is what a technician does rather than
    /// pressing a button.
    /// </remarks>
    public const int Page = 300;

    private readonly AgentControlLink _agent;

    private long? _stationLinkId;
    private string? _trigger;
    private bool _problemsOnly;
    private bool _busy;
    private bool _exhausted;
    private int _scanned;
    private int _resume;
    private bool _walkedBack;
    private string _problem = "";
    private string _message = "";

    /// <summary>
    /// The passes already on screen, so a repeat is not shown twice.
    /// </summary>
    /// <remarks>
    /// By the same natural key the detail is fetched with, which is unique for
    /// the reason given there.
    /// </remarks>
    private readonly HashSet<(DateTimeOffset At, string Unit)> _seen = [];
    private DateTimeOffset? _readAt;
    private PassRowViewModel? _selected;
    private PassDetailViewModel? _detail;
    private string _detailProblem = "";

    /// <param name="stationLinkId">
    /// The station this window opens filtered to, or <c>null</c> for the
    /// machine's own passes. Arriving pre-filtered is the point of the door
    /// on the station row: somebody who right-clicked Kisumu is asking about
    /// Kisumu.
    /// </param>
    public PassesViewModel(
        AgentControlLink agent,
        string machine,
        IReadOnlyList<StationChoice> stations,
        long? stationLinkId = null)
    {
        _agent = agent;
        _stationLinkId = stationLinkId;

        Machine = machine;
        Stations = [new StationChoice(null, "All stations"), .. stations];

        RefreshCommand = new AsyncCommand(RefreshAsync, Failed, () => !_busy);
        MoreCommand = new AsyncCommand(MoreAsync, Failed, () => !_busy && CanLoadMore);
    }

    /// <summary>The machine, as the title says it.</summary>
    public string Machine { get; }

    /// <summary>
    /// The window's title: what it lists, and which machine's.
    /// </summary>
    /// <remarks>
    /// The machine is named because this window's whole output -- the rows on
    /// screen, and the bundle Save these writes -- ends up in an email beside
    /// somebody else's, and a page of counts with no machine on it is a page
    /// nobody at HQ can file.
    /// </remarks>
    public string Title => string.Create(
        CultureInfo.CurrentCulture, $"Collection passes — {Machine}");

    /// <summary>The rows, oldest arriving at the bottom as more are loaded.</summary>
    public ObservableCollection<PassRowViewModel> Rows { get; } = [];

    /// <summary>What the station filter offers, "All stations" first.</summary>
    public IReadOnlyList<StationChoice> Stations { get; }

    /// <summary>What the trigger filter offers.</summary>
    public IReadOnlyList<TriggerChoice> Triggers { get; } =
    [
        new(null, "Any trigger"),
        new(CycleTriggers.Scheduled, "Scheduled"),
        new(CycleTriggers.Reconciliation, "Reconciliation sweep"),
        new(CycleTriggers.Collect, "Collect now"),
    ];

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand MoreCommand { get; }

    /// <summary>The station the table is filtered to, or null for all of them.</summary>
    public StationChoice SelectedStation
    {
        get => Stations.FirstOrDefault(choice => choice.StationLinkId == _stationLinkId)
            ?? Stations[0];
        set
        {
            if (_stationLinkId == value.StationLinkId)
            {
                return;
            }

            _stationLinkId = value.StationLinkId;

            Raise(nameof(SelectedStation));
            Raise(nameof(CountsHeading));

            RefreshCommand.Execute(null);
        }
    }

    public TriggerChoice SelectedTrigger
    {
        get => Triggers.FirstOrDefault(choice => choice.Trigger == _trigger) ?? Triggers[0];
        set
        {
            if (_trigger == value.Trigger)
            {
                return;
            }

            _trigger = value.Trigger;

            Raise(nameof(SelectedTrigger));

            RefreshCommand.Execute(null);
        }
    }

    /// <summary>Show only passes where something was wrong.</summary>
    public bool ProblemsOnly
    {
        get => _problemsOnly;
        set
        {
            if (!Set(ref _problemsOnly, value))
            {
                return;
            }

            RefreshCommand.Execute(null);
        }
    }

    /// <summary>
    /// What the counts column is a count of.
    /// </summary>
    /// <remarks>
    /// The heading changes with the filter, and that is not decoration. In a
    /// dump directory shared by forty stations a row's counts are the unit's,
    /// so a technician filtered to Banfora would read Bobo-Dioulasso's twelve
    /// failures as Banfora's. The counts become that station's own and the
    /// heading says whose they are, so the change is visible rather than
    /// silent.
    /// </remarks>
    public string CountsHeading => SelectedStation.StationLinkId is null
        ? "Unit totals"
        : SelectedStation.Name;

    /// <summary>True while a read is in flight.</summary>
    public bool IsBusy => _busy;

    /// <summary>When this table was read, so nobody reads a stale one as a quiet machine.</summary>
    public string ReadAt => _readAt is null
        ? ""
        : string.Create(
            CultureInfo.CurrentCulture,
            $"as of {_readAt.Value.ToLocalTime():HH:mm:ss}");

    /// <summary>Why there is nothing to show, when there is nothing.</summary>
    public string Problem => _problem;

    public bool HasRows => Rows.Count > 0;

    public bool HasProblem => _problem.Length > 0;

    /// <summary>
    /// Whether there is anything further back to fetch.
    /// </summary>
    /// <remarks>
    /// One button for one motion. Paging back and resuming past a read that
    /// gave up looking are the same request with the same cursor; only the
    /// sentence above the button differs, so a second affordance would be a
    /// distinction without a difference.
    /// </remarks>
    public bool CanLoadMore => !_exhausted && Rows.Count < Ceiling;

    /// <summary>
    /// What the line above the button says: three answers that look alike and
    /// are not.
    /// </summary>
    /// <remarks>
    /// "Twelve passes had problems" and "twelve, in the twenty thousand I got
    /// through before I stopped looking" are the same twelve rows and opposite
    /// facts. Saying only the first is the silent truncation this whole record
    /// was built to end.
    /// </remarks>
    public string Reach
    {
        get
        {
            if (!HasRows && !HasProblem)
            {
                return "";
            }

            var counted = string.Create(
                CultureInfo.CurrentCulture,
                $"{Rows.Count} {(Rows.Count == 1 ? "pass" : "passes")}{Narrowed()}.");

            if (Rows.Count >= Ceiling)
            {
                return counted
                    + " That is as many as this window holds. Narrow the station, or tick "
                    + "Problems only, to reach further back.";
            }

            if (_exhausted)
            {
                return counted + " That is all this machine has recorded.";
            }

            return _exhausted
                ? counted
                : counted + string.Create(
                    CultureInfo.CurrentCulture,
                    $" Read from the {_scanned} most recent; more are further back.");
        }
    }

    /// <summary>The row somebody has opened, and what it turned out to hold.</summary>
    public PassRowViewModel? SelectedPass
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
            {
                return;
            }

            _detail = null;
            _detailProblem = "";

            DetailChanged();

            if (value is not null)
            {
                _ = ReadDetailAsync(value);
            }
        }
    }

    public PassDetailViewModel? Detail => _detail;

    public bool HasDetail => _detail is not null;

    public string DetailProblem => _detailProblem;

    public bool HasDetailProblem => _detailProblem.Length > 0;

    /// <summary>The filter as the service is asked for it.</summary>
    public CyclePassQuery Query => new(_stationLinkId, _trigger, _problemsOnly, Most: Page);

    /// <summary>What just happened, when something did.</summary>
    /// <remarks>
    /// Where a saved bundle's path goes, and where the two things that can go
    /// wrong away from the table go: a clipboard another process is holding,
    /// and a file the service could not write.
    /// </remarks>
    public string Message => _message;

    public bool HasMessage => _message.Length > 0;

    /// <summary>
    /// Write a diagnostics bundle carrying what this window is showing.
    /// </summary>
    /// <remarks>
    /// The filter travels with the path, so what reaches HQ is what the
    /// technician found. The service writes the file, because the logs live
    /// beside the device token in a folder this program cannot read.
    /// </remarks>
    public async Task SaveAsync(string path)
    {
        Say("Collecting diagnostics…");

        var written = await _agent.SaveDiagnosticsAsync(path, Query).ConfigureAwait(true);

        Say(written.Value is null
            ? written.Detail ?? "The agent could not write a diagnostics file."
            : string.Create(
                CultureInfo.CurrentCulture,
                $"Saved to {written.Value.Path} ({Display.Size(written.Value.Bytes)})."));
    }

    /// <summary>Say something on the window's own line.</summary>
    public void Say(string message)
    {
        _message = message;

        Raise(nameof(Message));
        Raise(nameof(HasMessage));
    }

    /// <summary>
    /// Open this pass, or close it if it is the one already open.
    /// </summary>
    /// <remarks>
    /// A second click on an open row shuts it, which is what a row that
    /// expands ought to do and what a grid does not do on its own: selecting
    /// an already-selected row leaves it selected, so the detail would stay
    /// open until some other row was clicked. On a table whose rows open to
    /// several tables of their own, that means the only way to get back to a
    /// plain list is to open something else.
    /// <para>
    /// Here rather than in the window so that the rule is one a test can
    /// state. What the window contributes is which click counts -- a press
    /// inside the open detail is not a press on the row.
    /// </para>
    /// </remarks>
    public void Toggle(PassRowViewModel row) =>
        SelectedPass = ReferenceEquals(_selected, row) ? null : row;

    /// <summary>
    /// Point an already-open window at a station.
    /// </summary>
    /// <remarks>
    /// The window is single-instance, so a technician who right-clicks a
    /// second station while it is open would otherwise be shown the one they
    /// opened first -- focused, unchanged, and with nothing to say the filter
    /// they asked for had been dropped.
    /// </remarks>
    public void FilterTo(long? stationLinkId) =>
        SelectedStation = Stations.FirstOrDefault(
            choice => choice.StationLinkId == stationLinkId) ?? Stations[0];

    /// <summary>
    /// Whether coming back to this window should re-read it.
    /// </summary>
    /// <remarks>
    /// Only while it is still showing the newest page. History does not
    /// change, so a technician who has walked back through four pages looking
    /// for something is reading a part of the log that a refresh could only
    /// throw away -- and closing the Save dialog is enough to raise this.
    /// </remarks>
    public bool RefreshOnReturn => !_walkedBack && !_busy;

    /// <summary>Read the newest page, throwing away what is on screen.</summary>
    public async Task RefreshAsync()
    {
        Rows.Clear();
        _seen.Clear();
        SelectedPass = null;

        _exhausted = false;
        _scanned = 0;
        _resume = 0;
        _walkedBack = false;

        await ReadAsync(Query).ConfigureAwait(true);
    }

    /// <summary>Fetch the next page and add it below what is already there.</summary>
    /// <remarks>
    /// Appending rather than replacing, because the motion this window exists
    /// for is walking backwards until something is found, and losing your
    /// place on every press is what makes that unbearable.
    /// </remarks>
    public async Task MoreAsync()
    {
        if (!CanLoadMore)
        {
            return;
        }

        _walkedBack = true;

        await ReadAsync(Query with { Skip = _resume }).ConfigureAwait(true);
    }

    private async Task ReadAsync(CyclePassQuery query)
    {
        Busy(true);

        try
        {
            var answer = await _agent.PassesAsync(query).ConfigureAwait(true);

            _readAt = DateTimeOffset.Now;

            if (answer.Value is null)
            {
                _problem = answer.Detail
                    ?? "This machine's record of what it has done could not be read.";

                return;
            }

            foreach (var row in answer.Value.Rows)
            {
                if (Rows.Count >= Ceiling)
                {
                    break;
                }

                // A pass arriving at the top between one page and the next
                // shifts the window a little, so a row can come back twice.
                // Dropped here rather than prevented there, because a cursor
                // that could not repeat could only be one that dropped
                // instead, and a repeat is the safe direction to be wrong in.
                if (_seen.Add((row.At, row.Unit)))
                {
                    Rows.Add(new PassRowViewModel(row));
                }
            }

            _exhausted = answer.Value.Exhausted;
            _scanned += answer.Value.Scanned;
            _resume = answer.Value.Resume;
            _problem = Rows.Count > 0 ? "" : Nothing();
        }
        finally
        {
            Busy(false);
        }
    }

    /// <summary>
    /// What to say when the table is empty, which is never just "no rows".
    /// </summary>
    /// <remarks>
    /// A machine that has not collected yet, a filter nothing matches, and a
    /// read that gave up looking are three different states that all render
    /// as an empty table. An empty table is the absence this whole record
    /// exists to replace with a sentence.
    /// </remarks>
    private string Nothing()
    {
        if (!_exhausted)
        {
            var read = string.Create(
                CultureInfo.CurrentCulture,
                $"Nothing{Narrowed()} in the {_scanned} most recent passes.");

            return read + " There are older ones further back: press Load more to keep looking.";
        }

        return _stationLinkId is null && _trigger is null && !_problemsOnly
            ? "This machine has not recorded a collection pass yet."
            : $"No pass{Narrowed()} is in this machine's record.";
    }

    /// <summary>The filter, said in the sentence rather than left to be inferred.</summary>
    private string Narrowed()
    {
        var narrowed = new List<string>();

        if (SelectedStation.StationLinkId is not null)
        {
            narrowed.Add($"for {SelectedStation.Name}");
        }

        if (_trigger is not null)
        {
            narrowed.Add($"started by {SelectedTrigger.Name.ToLowerInvariant()}");
        }

        if (_problemsOnly)
        {
            narrowed.Add("with problems");
        }

        return narrowed.Count == 0 ? "" : " " + string.Join(", ", narrowed);
    }

    private async Task ReadDetailAsync(PassRowViewModel row)
    {
        var answer = await _agent.PassAsync(row.At, row.Unit).ConfigureAwait(true);

        // The selection may have moved on while this was travelling: a
        // technician clicking down a list faster than the pipe answers would
        // otherwise see one row's detail under another row's heading.
        if (!ReferenceEquals(_selected, row))
        {
            return;
        }

        if (answer.Value is null)
        {
            _detailProblem = answer.Detail ?? "This pass could not be read.";
        }
        else if (answer.Value.Record is null)
        {
            // Ordinary, on a machine working through a backlog: the log has
            // written its ceiling's worth over this pass since the row was
            // drawn.
            _detailProblem =
                "This pass is no longer in the machine's log — it has been written over since "
                + "this list was read.";
        }
        else
        {
            _detail = new PassDetailViewModel(answer.Value.Record);
        }

        DetailChanged();
    }

    /// <summary>The detail pane has something new, or nothing, to show.</summary>
    private void DetailChanged()
    {
        Raise(nameof(Detail));
        Raise(nameof(HasDetail));
        Raise(nameof(DetailProblem));
        Raise(nameof(HasDetailProblem));
    }

    private void Busy(bool running)
    {
        _busy = running;

        Raise(nameof(IsBusy));
        Raise(nameof(HasRows));
        Raise(nameof(HasProblem));
        Raise(nameof(Problem));
        Raise(nameof(Reach));
        Raise(nameof(ReadAt));
        Raise(nameof(CanLoadMore));

        RefreshCommand.Refresh();
        MoreCommand.Refresh();
    }

    /// <summary>
    /// Something went wrong in this window rather than in the agent.
    /// </summary>
    /// <remarks>
    /// Public because the window's own handlers are async void: nothing above
    /// them can catch anything, and an exception escaping one would take down
    /// the one program on the machine whose job is to explain what is wrong.
    /// </remarks>
    public void Failed(Exception exception)
    {
        _problem = $"Something went wrong in this window: {exception.Message}";

        Raise(nameof(Problem));
        Raise(nameof(HasProblem));
    }
}

/// <summary>One entry in the station filter.</summary>
/// <param name="StationLinkId">Null for "All stations".</param>
public sealed record StationChoice(long? StationLinkId, string Name);

/// <summary>One entry in the trigger filter.</summary>
public sealed record TriggerChoice(string? Trigger, string Name);
