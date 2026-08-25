using AdlAgent.Core.Api;
using AdlAgent.TestSupport;
using AdlAgent.Tray;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The two panes as things to read and navigate: the hint that says what the
/// connection list is for, the heading that says what the grid is a list of,
/// and the read-only status a row can be opened into.
/// </summary>
/// <remarks>
/// Driven through <see cref="ShellViewModel"/> over the real transport, the
/// same distance as the rest of the local UI.
/// <para>
/// What is under test here is mostly one distinction: this class assigning
/// the selection itself, versus a technician clicking. They arrive at the
/// same setter, and everything about whether the pane still needs explaining
/// hangs on telling them apart.
/// </para>
/// </remarks>
public class StationPaneTests
{
    private const string Vaisala = "C:\\VendorData\\Vaisala";
    private const string Campbell = "C:\\VendorData\\Campbell";

    // ---------- the hint ----------

    [Fact]
    public async Task The_pane_explains_itself_even_though_a_connection_is_already_selected()
    {
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]),
            SyncConfigs.Connection(4, "Campbell", stationLinks: [SyncConfigs.Link(21, Campbell)]));

        var window = shown.Window;

        // The rule cannot be "nothing is selected yet". Choose() picks a
        // connection from the machine's first answer, so there is no moment
        // after the window is drawn when nothing is -- and a hint keyed on
        // that would never once be read.
        Assert.NotNull(window.SelectedConnection);
        Assert.True(window.ShowsConnectionHint);
    }

    [Fact]
    public async Task Picking_a_connection_puts_the_hint_away_for_good()
    {
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]),
            SyncConfigs.Connection(4, "Campbell", stationLinks: [SyncConfigs.Link(21, Campbell)]));

        var window = shown.Window;

        window.SelectedConnection = window.Connections[1];

        Assert.False(window.ShowsConnectionHint);

        // And back again. Somebody who has understood the pane does not need
        // telling a second time, and a hint that came back would be one this
        // window nagged with.
        window.SelectedConnection = window.Connections[0];

        Assert.False(window.ShowsConnectionHint);
    }

    [Fact]
    public async Task A_machine_with_nothing_linked_to_it_has_nothing_to_hint_about()
    {
        await using var shown = await Showing();

        // The tab draws a sentence about the machine in place of both panes
        // here. A hint telling somebody to click one of no connections would
        // be sitting on top of it.
        Assert.Empty(shown.Window.Connections);
        Assert.False(shown.Window.ShowsConnectionHint);
    }

    [Fact]
    public async Task The_poll_restoring_the_selection_is_not_somebody_choosing()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.True(window.ShowsConnectionHint);

        // A rebuild writes the selection three times -- null, then the
        // restored connection, then its first station. Every one of them
        // arrives at the same setter a click does, and if they counted, the
        // hint would be gone within five seconds of the window opening on
        // every machine ADL ever changes anything on.
        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala),
                SyncConfigs.Link(12, Vaisala),
            ]));

        await agent.Configuration.RefreshAsync();
        await window.RefreshAsync();

        Assert.Equal(2, window.SelectedConnection!.Stations.Count);
        Assert.True(window.ShowsConnectionHint);
    }

    [Fact]
    public async Task A_dismissed_hint_stays_dismissed_across_a_rebuild()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]),
            SyncConfigs.Connection(4, "Campbell", stationLinks: [SyncConfigs.Link(21, Campbell)]));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        window.SelectedConnection = window.Connections[1];

        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]),
            SyncConfigs.Connection(4, "Campbell", stationLinks:
            [
                SyncConfigs.Link(21, Campbell),
                SyncConfigs.Link(22, Campbell),
            ]));

        await agent.Configuration.RefreshAsync();
        await window.RefreshAsync();

        Assert.False(window.ShowsConnectionHint);
    }

    // ---------- what the grid says it is a list of ----------

    [Fact]
    public async Task The_grid_names_the_connection_its_rows_are_from()
    {
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]),
            SyncConfigs.Connection(4, "Campbell", stationLinks: [SyncConfigs.Link(21, Campbell)]));

        var window = shown.Window;

        // The Connection column went when the pane arrived, and that left the
        // grid's scope stated nowhere but by a highlight in another control.
        Assert.Equal("Station links for Vaisala AWS", window.StationsHeading);

        window.SelectedConnection = window.Connections[1];

        Assert.Equal("Station links for Campbell", window.StationsHeading);
    }

    [Fact]
    public async Task A_machine_with_no_connections_heads_the_grid_with_nothing()
    {
        await using var shown = await Showing();

        // Rather than "Station links for", which is a sentence broken off
        // mid-way sitting above an explanation of why there is no list.
        Assert.Equal("", shown.Window.StationsHeading);
    }

    // ---------- the status a row opens into ----------

    [Fact]
    public async Task Checking_a_station_counts_what_is_in_its_folder_now()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
                [SyncConfigs.Link(11, Vaisala, "KAK_*.dat")]));

        agent.Files.Add(Vaisala, "KAK_20260821.dat", agent.Time.GetUtcNow());
        agent.Files.Add(Vaisala, "KAK_20260822.dat", agent.Time.GetUtcNow());
        agent.Files.Add(Vaisala, "GAR_20260822.dat", agent.Time.GetUtcNow());

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var status = window.BeginWatching();

        Assert.NotNull(status);

        await status.CheckAsync();

        // The only line on that window that is true of this machine at the
        // moment somebody is reading it. Everything else there is a memory of
        // the last sync -- and the third file is there so the answer is the
        // pattern's count rather than the folder's.
        Assert.Contains("2 files match", status.Station.MatchSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checking_a_station_leaves_the_row_behind_it_alone()
    {
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]));

        var window = shown.Window;
        var row = window.SelectedStation!;

        var status = window.BeginWatching()!;

        await status.CheckAsync();

        // A copy, for the same reason the settings window edits one: the
        // probe writes a sentence onto the station it probed, and the row is
        // not a thing to write on.
        Assert.NotSame(row, status.Station);
        Assert.Empty(row.MatchSummary);
    }

    [Fact]
    public async Task A_station_with_no_folder_bound_says_so_rather_than_counting_nothing()
    {
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, folder: "")]));

        var status = shown.Window.BeginWatching()!;

        await status.CheckAsync();

        // A station nobody has bound yet, which is the state most rows on a
        // fresh install are in. "0 files match" would be a true sentence
        // about a folder that was never named, and it sends a technician
        // looking for files rather than for the box they have not filled in.
        Assert.NotEmpty(status.Station.MatchSummary);
        Assert.DoesNotContain("0 files match", status.Station.MatchSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_rows_stop_moving_while_the_status_window_is_open()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var status = window.BeginWatching()!;

        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala),
                SyncConfigs.Link(12, Vaisala),
            ]));

        await agent.Configuration.RefreshAsync();
        await window.RefreshAsync();

        // The window holds a copy of a row. Replacing the rows underneath it
        // would leave it describing a station the list no longer contains.
        Assert.Single(window.SelectedConnection!.Stations);

        status.Done();

        await window.RefreshAsync();

        Assert.Equal(2, window.SelectedConnection!.Stations.Count);
    }

    [Fact]
    public async Task Checking_a_station_with_no_row_selected_opens_nothing()
    {
        await using var shown = await Showing();

        // Reachable from the menu on a grid that has no rows in it, and the
        // honest answer is no window rather than one describing nothing.
        Assert.Null(shown.Window.BeginWatching());
    }

    // ---------- the facts the grid has no room for ----------

    [Fact]
    public async Task A_row_carries_the_identifiers_and_the_standing_the_grid_cannot_show()
    {
        await using var shown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
                [SyncConfigs.Link(11, Vaisala, enabled: false)]));

        var station = shown.Window.SelectedStation!;

        Assert.Equal(11, station.StationLinkId);
        Assert.Equal("STATION11", station.StationId);

        // Off is off because HQ switched it -- or its whole connection -- off,
        // and a technician reading that beside a folder they have just bound
        // needs telling the folder is not the problem.
        Assert.False(station.Enabled);
        Assert.Contains("Switched off in ADL", station.Standing);
    }

    /// <summary>
    /// A window already showing these connections, and the harness under it.
    /// </summary>
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
        public ShellViewModel Window { get; } = window;

        public async ValueTask DisposeAsync()
        {
            serving.Dispose();

            await agent.DisposeAsync();
        }
    }
}
