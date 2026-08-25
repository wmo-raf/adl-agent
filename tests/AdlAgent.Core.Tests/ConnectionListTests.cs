using AdlAgent.Core.Api;
using AdlAgent.TestSupport;
using AdlAgent.Tray;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The station list, split by the connection each station is under.
/// </summary>
/// <remarks>
/// Driven through <see cref="ShellViewModel"/> over the real transport,
/// against a real control service and the fake ADL behind it -- the same
/// distance the rest of the local UI is tested at.
/// <para>
/// The list was flat, with the connection repeated down a column, and two
/// facts had nowhere to be said as a result: a connection ADL had switched
/// off arrived only as a false on each of its stations, so the window blamed
/// the stations; and a connection with no station links left no trace at all,
/// so an administrator who had made one and not yet linked to it looked, from
/// the machine, exactly like one who had done nothing. Most of what is below
/// is about those two.
/// </para>
/// </remarks>
public class ConnectionListTests
{
    private const string Vaisala = "C:\\VendorData\\Vaisala";
    private const string Campbell = "C:\\VendorData\\Campbell";

    // ---------- the two panes ----------

    [Fact]
    public async Task A_machine_serving_two_vendors_lists_both_with_their_own_stations()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala),
                SyncConfigs.Link(12, Vaisala),
            ]),
            SyncConfigs.Connection(4, "Campbell", stationLinks:
            [
                SyncConfigs.Link(21, Campbell),
            ]));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal(
            ["Vaisala AWS", "Campbell"],
            window.Connections.Select(each => each.ConnectionName));

        Assert.Equal([11L, 12L], window.Connections[0].Stations.Select(each => each.StationLinkId));
        Assert.Equal([21L], window.Connections[1].Stations.Select(each => each.StationLinkId));
    }

    [Fact]
    public async Task Choosing_a_connection_selects_its_first_station()
    {
        await using var windowShown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]),
            SyncConfigs.Connection(4, "Campbell", stationLinks: [SyncConfigs.Link(21, Campbell)]));

        var window = windowShown.Window;

        window.SelectedConnection = window.Connections[1];

        // Rather than nothing, which would grey out "Edit settings…" and make
        // every move between vendors cost two clicks instead of one.
        Assert.Equal(21, window.SelectedStation!.StationLinkId);
        Assert.True(window.HasSelectedStation);
    }

    // ---------- the two facts a flat list could not carry ----------

    [Fact]
    public async Task A_connection_with_no_stations_is_listed_rather_than_absent()
    {
        await using var windowShown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]),
            SyncConfigs.Connection(4, "Campbell"));

        var window = windowShown.Window;

        // The state of every connection an administrator has just made. In a
        // flat list of stations it left no trace at all, so nobody at the
        // machine could tell it apart from an administrator who had done
        // nothing.
        var campbell = window.Connections.Single(each => each.ConnectionName == "Campbell");

        Assert.True(campbell.HasNoStations);
        Assert.Equal("0 stations", campbell.StationCount);
        Assert.Contains("No stations linked", campbell.Standing);
    }

    [Fact]
    public async Task A_switched_off_connection_says_so_rather_than_blaming_its_stations()
    {
        await using var windowShown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", enabled: false, stationLinks:
            [
                SyncConfigs.Link(11, Vaisala),
                SyncConfigs.Link(12, Vaisala),
            ]));

        var window = windowShown.Window;

        var vaisala = window.Connections.Single();

        Assert.False(vaisala.Enabled);
        Assert.Contains("Switched off in ADL", vaisala.Standing);

        // Green, not amber. An administrator switched it off deliberately,
        // there is nothing on this machine to fix, and a warning colour would
        // send a technician hunting for a fault that does not exist.
        Assert.Equal(TrayState.Working, vaisala.Attention);
    }

    [Fact]
    public async Task A_switched_off_connection_is_told_apart_from_stations_switched_off_one_by_one()
    {
        await using var offShown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", enabled: false, stationLinks:
                [SyncConfigs.Link(11, Vaisala)]));

        var off = offShown.Window;

        await using var individuallyShown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
                [SyncConfigs.Link(11, Vaisala, enabled: false)]));

        var individually = individuallyShown.Window;

        // Both leave every station disabled, which is all the flat list ever
        // carried. They are different problems for different people, and the
        // second one is the administrator having switched off one station.
        Assert.Contains("Switched off in ADL", off.Connections.Single().Standing);
        Assert.Contains("Every station switched off", individually.Connections.Single().Standing);
    }

    // ---------- what each row says about itself ----------

    [Fact]
    public async Task A_connection_row_counts_the_stations_waiting_for_a_folder()
    {
        await using var windowShown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Vaisala),
                SyncConfigs.Link(12, folder: ""),
                SyncConfigs.Link(13, folder: ""),
            ]));

        var window = windowShown.Window;

        var vaisala = window.Connections.Single();

        // The point of the pane existing: without this a technician would
        // click every connection in turn to find out whether there was
        // anything in it for them, which is worse than the one grid it
        // replaced.
        Assert.Equal("2 stations need a folder", vaisala.Standing);
        Assert.Equal(TrayState.NeedsAttention, vaisala.Attention);
    }

    [Fact]
    public async Task A_connection_with_nothing_to_do_says_so_in_green()
    {
        await using var windowShown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]));

        var window = windowShown.Window;

        var vaisala = window.Connections.Single();

        Assert.Equal("Collecting", vaisala.Standing);
        Assert.Equal("1 station", vaisala.StationCount);
        Assert.Equal(TrayState.Working, vaisala.Attention);
    }

    [Fact]
    public async Task A_row_and_the_line_above_it_agree_because_they_are_one_ladder()
    {
        await using var windowShown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, folder: "")]));

        var window = windowShown.Window;

        // The whole reason the ladder was extracted rather than written
        // twice. These two sentences are on screen together, and a row
        // reading "1 station needs a folder" beside a line reading "nothing
        // to do" is the window telling a technician two different things
        // about one machine.
        Assert.Equal(NextStepKind.BindAFolder, window.NextStep.Kind);
        Assert.Equal("1 station needs a folder", window.Connections.Single().Standing);
    }

    // ---------- the line that has to be followable ----------

    [Fact]
    public async Task The_next_step_names_the_connection_so_the_instruction_can_be_followed()
    {
        await using var windowShown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]),
            SyncConfigs.Connection(4, "Campbell", stationLinks: [SyncConfigs.Link(21, folder: "")]));

        var window = windowShown.Window;

        // "Open the Stations tab, select it" was a complete instruction while
        // every station was in one grid. With the list split it stops being
        // one unless the line says which side of the split to look on.
        Assert.Equal(NextStepKind.BindAFolder, window.NextStep.Kind);
        Assert.Contains("Station 21, under Campbell:", window.NextStep.Text);
    }

    [Fact]
    public async Task The_window_opens_on_the_connection_the_line_is_pointing_at()
    {
        await using var windowShown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]),
            SyncConfigs.Connection(4, "Campbell", stationLinks: [SyncConfigs.Link(21, folder: "")]));

        var window = windowShown.Window;

        // Not the first connection. The other half of the same fix: the line
        // names where to go, and the window is already there.
        Assert.Equal("Campbell", window.SelectedConnection!.ConnectionName);
        Assert.Equal(21, window.SelectedStation!.StationLinkId);
    }

    [Fact]
    public async Task A_machine_with_nothing_to_do_opens_on_the_first_connection()
    {
        await using var windowShown = await Showing(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]),
            SyncConfigs.Connection(4, "Campbell", stationLinks: [SyncConfigs.Link(21, Campbell)]));

        var window = windowShown.Window;

        Assert.Equal(NextStepKind.NothingToDo, window.NextStep.Kind);
        Assert.Equal("Vaisala AWS", window.SelectedConnection!.ConnectionName);
    }

    // ---------- the poll ----------

    [Fact]
    public async Task A_connection_with_no_stations_appearing_is_noticed_by_the_poll()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Single(window.Connections);

        // The case the old change-detection was blind to. A connection with
        // no station links leaves the flat station list byte-identical, so a
        // comparison over the stations alone would never rebuild -- and the
        // new connection would stay invisible for as long as the tray ran.
        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]),
            SyncConfigs.Connection(4, "Campbell"));

        await agent.Configuration.RefreshAsync();
        await window.RefreshAsync();

        Assert.Equal(
            ["Vaisala AWS", "Campbell"],
            window.Connections.Select(each => each.ConnectionName));
    }

    [Fact]
    public async Task A_switched_off_connection_with_no_stations_is_noticed_by_the_poll()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.Serving(SyncConfigs.Connection(4, "Campbell"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Contains("No stations linked", window.Connections.Single().Standing);

        // Same blind spot, other direction: switching off a connection that
        // has no stations changes nothing whatever about the flat list.
        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(4, "Campbell", enabled: false));

        await agent.Configuration.RefreshAsync();
        await window.RefreshAsync();

        Assert.Contains("Switched off in ADL", window.Connections.Single().Standing);
    }

    [Fact]
    public async Task The_poll_keeps_the_connection_a_technician_was_reading()
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

        window.SelectedConnection = window.Connections.Single(each => each.ConnectionId == 4);

        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]),
            SyncConfigs.Connection(4, "Campbell", stationLinks:
            [
                SyncConfigs.Link(21, Campbell),
                SyncConfigs.Link(22, Campbell),
            ]));

        await agent.Configuration.RefreshAsync();
        await window.RefreshAsync();

        // Restored by id across a rebuild. A poll that dropped somebody back
        // to the first connection every few seconds would make the pane
        // unusable on the machine it is for.
        Assert.Equal(4, window.SelectedConnection!.ConnectionId);
        Assert.Equal(2, window.SelectedConnection.Stations.Count);
    }

    [Fact]
    public async Task A_connection_ADL_drops_falls_back_to_the_first_without_saying_anything()
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

        window.SelectedConnection = window.Connections.Single(each => each.ConnectionId == 4);

        // A real answer to a real button, so the assertion below is about the
        // poll leaving it alone rather than about it never having been set.
        window.PairingCode = "not-a-code";

        await window.PairAsync();

        var answered = window.Message;

        Assert.NotEmpty(answered);

        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]));

        await agent.Configuration.RefreshAsync();
        await window.RefreshAsync();

        Assert.Equal(3, window.SelectedConnection!.ConnectionId);

        // And the message is untouched. It is the answer to the last button
        // somebody pressed, not a place for the poll to narrate itself: a
        // technician reading a save confirmation should not have it wiped by
        // an administrator's unrelated edit.
        Assert.Equal(answered, window.Message);
    }

    [Fact]
    public async Task The_window_does_not_re_pick_the_connection_on_every_poll()
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

        Assert.Equal(3, window.SelectedConnection!.ConnectionId);

        // Campbell now wants somebody. The line moves to it; the selection
        // must not, or a technician reading Vaisala would be dragged away
        // mid-sentence, and again every cycle after that.
        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Vaisala)]),
            SyncConfigs.Connection(4, "Campbell", stationLinks: [SyncConfigs.Link(21, folder: "")]));

        await agent.Configuration.RefreshAsync();
        await window.RefreshAsync();

        Assert.Equal(NextStepKind.BindAFolder, window.NextStep.Kind);
        Assert.Equal(3, window.SelectedConnection!.ConnectionId);
    }

    /// <summary>
    /// A window already showing these connections, and the harness under it.
    /// </summary>
    /// <remarks>
    /// Returned together, and disposable, because the harness owns a served
    /// socket: a helper that handed back only the window would leak one per
    /// test.
    /// </remarks>
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
