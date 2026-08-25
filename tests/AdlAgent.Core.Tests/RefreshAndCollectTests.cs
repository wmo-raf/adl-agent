using AdlAgent.TestSupport;
using AdlAgent.Tray;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The two buttons that make the machine do something now: Refresh above the
/// connection list, and Collect now on a station row.
/// </summary>
/// <remarks>
/// Driven through <see cref="ShellViewModel"/> over the real transport, the
/// same distance as the rest of the local UI.
/// <para>
/// Both are started rather than performed, which is the control surface's
/// constraint rather than a taste: it serves one client at a time and times
/// out in three seconds, so a command that waited for an HTTP call -- let
/// alone an upload -- would freeze the tray's own status poll and then report
/// a working service as absent. What is tested here is mostly the other half
/// of that: how the answer to a press gets back to the person who pressed it.
/// </para>
/// </remarks>
public class RefreshAndCollectTests
{
    private const string Garissa = "C:\\VendorData\\Garissa";

    // ---------- Refresh ----------

    [Fact]
    public async Task Refresh_asks_ADL_and_the_answer_arrives_on_the_poll()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));

        await agent.PairAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        await window.SyncAsync();

        // Not the outcome. The command returns as soon as the call is under
        // way, and this is the sentence that says waiting is the right thing
        // to be doing.
        Assert.Contains("Asking ADL", window.Message, StringComparison.Ordinal);
        Assert.False(window.SyncCommand.CanExecute(null));

        await Settled(window, agent);

        Assert.Contains("Synced with ADL", window.Message, StringComparison.Ordinal);

        // And pressable again. A button left grey because an answer never
        // arrived is worse than one that can be pressed twice.
        Assert.True(window.SyncCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refresh_against_an_ADL_that_is_not_answering_says_so()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));

        await agent.PairAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        agent.Server.Unreachable = true;

        await window.SyncAsync();

        await Settled(window, agent);

        // The distinction the whole attempt record exists for. RefreshAsync
        // answers an unreachable ADL with the configuration off the disk
        // rather than with nothing -- which is right for the cycle, and which
        // would read here as a successful refresh that changed nothing.
        Assert.Contains("ADL is not answering", window.Message, StringComparison.Ordinal);
        Assert.True(window.SyncCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refresh_does_not_report_a_sync_this_window_did_not_ask_for()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));

        await agent.PairAsync();

        // Somebody else's press: another tray, on another logon session of
        // the same server.
        agent.Syncs.Start();

        await Eventually(() => agent.Syncs.Last is { FinishedAt: not null });

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        // Nothing announced. A window that reported the first finished
        // attempt it saw would claim somebody else's sync as the answer to a
        // button this one never had pressed.
        Assert.Empty(window.Message);
    }

    // ---------- Collect now ----------

    [Fact]
    public async Task Collect_now_starts_a_run_and_the_window_watches_it()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));
        agent.Files.Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var collect = await window.BeginCollectingAsync();

        Assert.NotNull(collect);
        Assert.Equal(11, collect.StationLinkId);
        Assert.Contains("Station 11", collect.Title, StringComparison.Ordinal);

        // What the window would do on its timer.
        await Watching(collect);

        Assert.False(collect.Running);
        Assert.Contains("1 sent", collect.Counts, StringComparison.Ordinal);
        Assert.Empty(collect.Problem);
    }

    [Fact]
    public async Task A_refused_collect_opens_no_window_and_leaves_the_rows_moving()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, folder: ""));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Null(await window.BeginCollectingAsync());

        // The service's own sentence, not one the window invented. HQ can
        // switch a station off between a row being drawn and the item on it
        // being pressed, so the refusal has to come from the thing that knows.
        Assert.Contains("No folder is bound", window.Message, StringComparison.Ordinal);

        // And the list is still alive. Freezing the rows behind a modal window
        // that never opened is how a station list comes to be frozen for the
        // rest of the session.
        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, folder: ""), SyncConfigs.Link(12, Garissa));

        await agent.Configuration.RefreshAsync();
        await window.RefreshAsync();

        Assert.Equal(2, window.SelectedConnection!.Stations.Count);
    }

    [Fact]
    public async Task Cancelling_stops_the_run_and_the_window_says_so()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));
        agent.Files.Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        using var manifested = new SemaphoreSlim(0, 1);

        agent.Server.BeforeManifest = async () => await manifested.WaitAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var collect = (await window.BeginCollectingAsync())!;

        await Eventually(() => agent.Cycle.Running);

        await collect.CancelAsync();

        manifested.Release();

        await Watching(collect);

        Assert.False(collect.Running);

        // Nothing is repaired by cancelling, and nothing needs to be: the
        // agent keeps no record of what it delivered, so whatever this run did
        // not reach is offered again by the next cycle.
        Assert.Contains("Stopped", collect.Step, StringComparison.Ordinal);
    }

    // ---------- what the row says about the item on it ----------

    [Fact]
    public async Task A_row_greys_Collect_now_and_says_which_of_the_two_reasons_it_is()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Garissa),
                SyncConfigs.Link(12, folder: ""),
                SyncConfigs.Link(13, Garissa, enabled: false),
            ]));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var stations = window.Connections.Single().Stations;

        Assert.True(stations[0].CanCollect);
        Assert.Empty(stations[0].CollectBlockedReason);

        // Two greys wanting two different people: one is the box on this
        // machine nobody has filled in, the other is HQ's decision and nothing
        // here to fix.
        Assert.False(stations[1].CanCollect);
        Assert.Contains("No folder is bound", stations[1].CollectBlockedReason, StringComparison.Ordinal);

        Assert.False(stations[2].CanCollect);
        Assert.Contains("Switched off in ADL", stations[2].CollectBlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_row_labels_a_requested_collect_rather_than_passing_it_off_as_a_cycle()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));
        agent.Files.Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal("1 seen, 1 sent, 0 failed", window.SelectedStation!.LastCycle);

        agent.Time.Advance(TimeSpan.FromMinutes(1));

        agent.Collects.Start(11);

        await Eventually(() => agent.Collects.Progress is { Running: false });
        await window.RefreshAsync();

        // Labelled, so a number from a run covering one station is never
        // mistaken for the machine's own cycle.
        //
        // One seen and none sent, and that is the agent working: it is
        // stateless, so it offers the file again and ADL's ledger answers "I
        // have that one". The label is what stops a technician reading the
        // zero as the collect having failed.
        Assert.Equal("on request: 1 seen, 0 sent, 0 failed", window.SelectedStation!.LastCycle);
    }

    /// <summary>Poll the run the way the window's timer does, until it stops.</summary>
    private static async Task Watching(CollectViewModel collect)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (collect.Running && DateTime.UtcNow < deadline)
        {
            await collect.PollAsync();
            await Task.Delay(10);
        }

        Assert.False(collect.Running, "The run never finished.");
    }

    /// <summary>Poll the window until the sync it asked for has been answered.</summary>
    private static async Task Settled(ShellViewModel window, AgentHarness agent)
    {
        await Eventually(() => agent.Syncs.Last is { FinishedAt: not null });

        await window.RefreshAsync();
    }

    private static async Task Eventually(Func<bool> settled)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            if (settled())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The agent never reached the state this test is about.");
    }

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
}
