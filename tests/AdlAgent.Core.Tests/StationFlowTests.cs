using AdlAgent.Core.Api;
using AdlAgent.Core.Status;
using AdlAgent.TestSupport;
using AdlAgent.Tray;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Whether data is actually reaching ADL, said on the row and everywhere the
/// row's verdict is summed up.
/// </summary>
/// <remarks>
/// The state this exists for is the one the window could not name: a station
/// configured perfectly, reporting no error, and sending nothing because the
/// logger died, the share unmounted, or the vendor changed what it writes and
/// the pattern stopped matching. Every other fact on a row is about what this
/// machine <em>did</em>, and all of it reads healthy for such a station.
/// <para>
/// Driven through <see cref="ShellViewModel"/> over the real transport, the
/// same distance as the rest of the local UI, because the point of the
/// verdict is that one decision reaches the dot, the connection's sentence and
/// the line at the top of the window without the three being able to
/// disagree.
/// </para>
/// </remarks>
public class StationFlowTests
{
    private const string Vaisala = "C:\\VendorData\\Vaisala";
    private const string Campbell = "C:\\VendorData\\Campbell";

    // ---------- the verdict itself ----------
    //
    // A pure function, asked directly. These are the rules rather than the
    // plumbing, and putting each of them behind a paired agent and an HTTP
    // server would buy nothing but twenty more harnesses running at once --
    // which the suite's real-time tests feel.

    [Fact]
    public void A_station_ADL_has_just_heard_from_is_collecting()
    {
        Assert.Equal(StationFlow.Collecting, Judge(lastReceivedAt: Now.AddMinutes(-20)));
    }

    [Fact]
    public void A_station_silent_past_its_window_has_gone_quiet()
    {
        Assert.Equal(StationFlow.Quiet, Judge(lastReceivedAt: Now.AddHours(-19)));
    }

    [Fact]
    public void A_station_nothing_has_ever_arrived_for_is_quiet_rather_than_a_state_of_its_own()
    {
        // Which is what makes the dot a confirmation signal for the person
        // binding a folder: the row is amber until the first file lands, and
        // green on the cycle after it does.
        Assert.Equal(StationFlow.Quiet, Judge(lastReceivedAt: null));
    }

    [Fact]
    public void A_station_with_no_folder_bound_is_blocked_rather_than_quiet()
    {
        // It is silent as well, necessarily. "Bind a folder" is the more
        // useful of the two things to be told, so it wins.
        Assert.Equal(StationFlow.Blocked, Judge(folder: "", lastReceivedAt: null));
        Assert.Equal(StationFlow.Blocked, Judge(folder: "   ", lastReceivedAt: null));
    }

    [Fact]
    public void A_station_that_reported_a_problem_is_blocked_however_recently_it_sent()
    {
        Assert.Equal(
            StationFlow.Blocked,
            Judge(error: "The folder could not be read.", lastReceivedAt: Now.AddMinutes(-1)));
    }

    [Fact]
    public void A_station_switched_off_in_ADL_is_not_judged_at_all()
    {
        // Green would assert that data is flowing for a station nothing is
        // scanned or sent for; amber would send a technician looking for a
        // fault that is an administrator's deliberate choice. Neither is
        // true of a station HQ silenced on purpose -- including one that has
        // no folder and has never sent anything, which is the state a
        // switched-off station is usually in.
        Assert.Equal(
            StationFlow.NotJudged,
            Judge(enabled: false, folder: "", error: "anything", lastReceivedAt: null));
    }

    // ---------- whose window ----------

    [Fact]
    public void A_vendor_that_writes_slowly_can_be_given_a_longer_window()
    {
        // Twenty hours of silence is a fault on a ten-minute vendor and a
        // Tuesday on a daily one. Without this the number would have to be
        // set to the slowest vendor in the country, which blunts it for every
        // fast one.
        Assert.Equal(
            StationFlow.Collecting,
            Judge(lastReceivedAt: Now.AddHours(-20), staleAfterMinutes: 1500));
    }

    [Fact]
    public void An_ADL_that_states_no_window_gets_the_same_six_hours_ADL_defaults_to()
    {
        // Only reachable against an ADL that predates the field. A machine
        // that quietly picked some other number would make two halves of one
        // system disagree about the same station.
        Assert.Equal(
            StationFlow.Collecting,
            Judge(lastReceivedAt: Now.AddHours(-5), staleAfterMinutes: null));
        Assert.Equal(
            StationFlow.Quiet,
            Judge(lastReceivedAt: Now.AddHours(-7), staleAfterMinutes: null));
    }

    [Fact]
    public void A_window_of_nothing_is_treated_as_no_window_rather_than_honoured()
    {
        // Zero would make every station on the connection permanently quiet,
        // which is a whole vendor's worth of rows saying "look here" about
        // nothing -- and the fastest way to teach somebody to stop reading
        // the column.
        Assert.Equal(
            StationFlow.Collecting,
            Judge(lastReceivedAt: Now.AddHours(-1), staleAfterMinutes: 0));
    }

    [Fact]
    public void A_file_dated_ahead_of_this_machines_clock_is_not_negative_time()
    {
        // A machine whose clock is behind ADL's produces exactly this, and
        // ADL measures that skew rather than pretending it cannot happen.
        Assert.Equal(StationFlow.Collecting, Judge(lastReceivedAt: Now.AddMinutes(30)));
        Assert.Equal("moments ago", Display.Ago(Now.AddMinutes(30), Now));
    }

    // ---------- how an age is written ----------

    [Theory]
    [InlineData(0, "moments")]
    [InlineData(1, "1 minute")]
    [InlineData(2, "2 minutes")]
    [InlineData(59, "59 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(120, "2 hours")]
    [InlineData(60 * 24 - 1, "23 hours")]
    [InlineData(60 * 24, "1 day")]
    [InlineData(60 * 24 * 4, "4 days")]
    public void An_age_is_one_unit_and_never_says_one_of_something_plural(
        int minutesAgo, string expected)
    {
        // One unit, the largest that is not zero. The exact moment is a
        // column of its own and never stale; what this adds is the reading
        // somebody does at a glance, and "19 hours" and "19 hours 14 minutes"
        // are the same reading.
        Assert.Equal(expected, Display.Span(Now.AddMinutes(-minutesAgo), Now));
    }

    // ---------- the verdict moves on its own ----------

    [Fact]
    public async Task A_station_crosses_its_window_without_any_other_fact_moving()
    {
        // The reason the verdict is decided on the snapshot rather than by the
        // row that draws it. Nothing about this station changes here -- ADL is
        // asked again and says exactly what it said before -- and the only
        // thing that has moved is the clock. A row that did its own
        // arithmetic would still be green, because the window rebuilds its
        // rows only when the snapshot changes.
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala, lastReceivedAt: TestClock.Start.AddHours(-5)),
            ]));

        Assert.Equal(StationFlow.Collecting, Row(shown).Flow);

        shown.Agent.Time.Advance(TimeSpan.FromHours(2));

        await shown.Window.RefreshAsync();

        Assert.Equal(StationFlow.Quiet, Row(shown).Flow);
    }

    // ---------- what the row says about it ----------

    [Fact]
    public async Task The_row_carries_the_moment_ADL_holds_and_the_age_beside_it()
    {
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala, lastReceivedAt: TestClock.Start.AddHours(-3)),
            ]));

        var row = Row(shown);

        // The cell is the moment, in this machine's own timezone, and never
        // goes stale. The age is the tooltip's, and is the one string here
        // that is wrong a moment after it is written.
        Assert.NotEqual("-", row.LastReceived);
        Assert.Equal("3 hours ago", row.LastReceivedAgo);
    }

    [Fact]
    public async Task A_quiet_row_says_how_long_it_has_been_quiet()
    {
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala, lastReceivedAt: TestClock.Start.AddHours(-19)),
            ]));

        Assert.Equal("Nothing has reached ADL for 19 hours.", Row(shown).FlowReason);
    }

    [Fact]
    public async Task A_row_nothing_ever_arrived_for_says_that_rather_than_inventing_a_history()
    {
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala, everReceived: false),
            ]));

        Assert.Equal(
            "ADL has never received a file for this station.", Row(shown).FlowReason);
    }

    [Fact]
    public async Task A_switched_off_row_says_it_is_switched_off_rather_than_showing_nothing()
    {
        // The grid has no other column saying so, which is what makes the
        // grey dot worth its width.
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala, enabled: false),
            ]));

        Assert.Contains("Switched off in ADL", Row(shown).FlowReason);
    }

    [Fact]
    public async Task An_age_advances_on_a_poll_that_rebuilt_nothing()
    {
        // The row is the same object throughout: ADL says exactly what it said
        // before, so the window rebuilds nothing, and the only thing that has
        // moved is the clock. An age that advanced only with a rebuild would
        // sit at "1 hour ago" all afternoon on a machine that is working.
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala, lastReceivedAt: TestClock.Start.AddHours(-1)),
            ]));

        var row = Row(shown);
        var raised = new List<string?>();

        row.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        Assert.Equal("1 hour ago", row.LastReceivedAgo);

        shown.Agent.Time.Advance(TimeSpan.FromHours(2));

        await shown.Window.RefreshAsync();

        Assert.Same(row, Row(shown));
        Assert.Equal("3 hours ago", row.LastReceivedAgo);
        Assert.Contains(nameof(StationViewModel.LastReceivedAgo), raised);
    }

    [Fact]
    public async Task A_poll_that_moved_no_age_says_nothing_about_any_row()
    {
        // Forty rows told the time every five seconds would be forty bindings
        // re-rendering the same two strings for ever. Only what reads
        // differently is announced.
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala, lastReceivedAt: TestClock.Start.AddHours(-1)),
            ]));

        var row = Row(shown);
        var raised = new List<string?>();

        row.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        shown.Agent.Time.Advance(TimeSpan.FromSeconds(5));

        await shown.Window.RefreshAsync();

        Assert.Empty(raised);
    }

    // ---------- and what it does to everything above the row ----------

    [Fact]
    public async Task A_connection_with_a_quiet_station_says_so_and_is_not_green()
    {
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala, lastReceivedAt: TestClock.Start.AddHours(-19)),
                SyncConfigs.Link(12, Campbell, lastReceivedAt: TestClock.Start.AddHours(-19)),
            ]));

        var connection = shown.Window.SelectedConnection!;

        Assert.Equal("2 stations have sent nothing", connection.Standing);
        Assert.Equal(TrayState.NeedsAttention, connection.Attention);
    }

    [Fact]
    public async Task A_station_that_failed_and_one_that_is_merely_silent_do_not_read_alike()
    {
        // The two rungs sit next to each other, and a technician reading one
        // of them cannot tell from the words which rung they are on unless
        // the words differ. The failing rung used to say "collected nothing",
        // which is what the silent one now says better.
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala),
            ]));

        shown.Agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, "Z:\\gone"),
            ]));

        await shown.Agent.Configuration.RefreshAsync();
        await shown.Agent.Cycle.RunAsync(CancellationToken.None);
        await shown.Window.RefreshAsync();

        Assert.Equal("1 station reported a problem", shown.Window.SelectedConnection!.Standing);
    }

    [Fact]
    public async Task The_line_at_the_top_names_a_quiet_station_and_says_what_to_do_about_it()
    {
        // An instruction rather than a statement, which is what an amber icon
        // in the corner of the screen is owed. Nobody here can know whether
        // the logger died or the pattern stopped matching -- but both are
        // answered by looking, and looking is a thing that can be told to
        // somebody.
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala, lastReceivedAt: TestClock.Start.AddHours(-19)),
            ]));

        var step = shown.Window.NextStep;

        Assert.Equal(NextStepKind.StationWentQuiet, step.Kind);
        Assert.Equal(TrayState.NeedsAttention, step.Attention);
        Assert.Contains("Station 11", step.Text);
        Assert.Contains("has sent nothing to ADL since", step.Text);
        Assert.Contains("check status", step.Text);
    }

    [Fact]
    public async Task The_line_names_a_station_that_never_started_without_dating_it()
    {
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala, everReceived: false),
            ]));

        Assert.Contains("has never sent anything to ADL", shown.Window.NextStep.Text);
    }

    [Fact]
    public async Task The_window_opens_on_the_connection_whose_station_the_line_names()
    {
        // The other half of an instruction being followable: a line reading
        // "Station 21, under Campbell, has sent nothing" opening on Vaisala
        // would be the window telling somebody to look somewhere it is not
        // showing.
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala),
            ]),
            SyncConfigs.Connection(4, "Campbell", stationLinks:
            [
                SyncConfigs.Link(21, Campbell, lastReceivedAt: TestClock.Start.AddHours(-19)),
            ]));

        Assert.Equal("Campbell", shown.Window.SelectedConnection!.ConnectionName);
    }

    [Fact]
    public async Task A_machine_whose_stations_are_all_switched_off_is_still_not_amber()
    {
        // Silent by an administrator's decision, so there is nothing here to
        // fix and nothing for the corner of the screen to say.
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala, enabled: false, everReceived: false),
            ]));

        Assert.Equal(TrayState.Working, shown.Window.NextStep.Attention);
        Assert.Equal(TrayState.Working, shown.Window.SelectedConnection!.Attention);
    }

    // ---------- driving it ----------

    private static readonly DateTimeOffset Now = TestClock.Start;

    private static StationFlow Judge(
        bool enabled = true,
        string folder = Vaisala,
        string? error = null,
        DateTimeOffset? lastReceivedAt = null,
        int? staleAfterMinutes = 360) =>
        StationFlows.Of(enabled, folder, error, lastReceivedAt, staleAfterMinutes, Now);

    private static StationViewModel Row(Shown shown) =>
        shown.Window.SelectedConnection!.Stations[0];

    private static async Task<Shown> Showing(params ConnectionConfig[] connections)
    {
        var agent = new AgentHarness();

        ServedAgent? serving = null;

        try
        {
            serving = await ServedAgent.ServingAsync(agent);

            agent.Server.Config = SyncConfigs.Serving(connections);

            await agent.PairAsync();
            await agent.Configuration.RefreshAsync();

            var window = new ShellViewModel(serving.Link);

            await window.RefreshAsync();

            return new Shown(agent, serving, window);
        }
        catch
        {
            serving?.Dispose();

            await agent.DisposeAsync();

            throw;
        }
    }

    private sealed class Shown(AgentHarness agent, ServedAgent serving, ShellViewModel window)
        : IAsyncDisposable
    {
        public AgentHarness Agent { get; } = agent;

        public ShellViewModel Window { get; } = window;

        public async ValueTask DisposeAsync()
        {
            serving.Dispose();

            await Agent.DisposeAsync();
        }
    }
}
