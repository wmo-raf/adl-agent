using AdlAgent.Core.Cycle;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The collect a technician asks for at the machine: one station, now, with
/// somebody watching.
/// </summary>
/// <remarks>
/// It is the scheduled cycle over a configuration narrowed to one station
/// link, which is the point: the sweep planner, the scanner, the pager and the
/// uploader are the ones the loop uses, so a station collected this way is
/// collected exactly as it would have been an hour later. Most of what is
/// below is about the three places that equivalence has to be broken -- the
/// sweep, the gate, and where the result is put -- and why.
/// </remarks>
public class CollectNowTests
{
    private const string Garissa = "C:\\VendorData\\Garissa";
    private const string Kisumu = "C:\\VendorData\\Kisumu";

    // ---------- one station, and only one ----------

    [Fact]
    public async Task Collecting_a_station_sends_its_files_and_leaves_its_neighbours_alone()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Garissa, "GARISSA_*.dat"),
                SyncConfigs.Link(12, Kisumu, "KISUMU_*.dat"),
            ]));

        agent.Files
            .Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n")
            .Add(Kisumu, "KISUMU_20260821.dat", Settled(agent), "09:00,19.2\n");

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var came = await agent.Cycle
            .CollectStationAsync(11, ICollectWatcher.Nobody, CancellationToken.None);

        Assert.NotNull(came);
        Assert.Equal(1, came.Uploaded);

        Assert.Equal("09:00,21.4\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);

        // The whole of what "for this station" means. Narrowing the
        // configuration rather than filtering inside the scan is what gets
        // this for free -- Kisumu's folder is never walked, so a machine with
        // forty stations does not pay for thirty-nine of them.
        Assert.Null(agent.Server.Held(12, "KISUMU_20260821.dat"));
    }

    [Fact]
    public async Task A_collect_sweeps_the_folder_rather_than_only_the_candidate_window()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(
            11,
            Garissa,
            "*.dat",
            watermark: agent.Time.GetUtcNow() - TimeSpan.FromHours(1),
            startDate: agent.Time.GetUtcNow() - TimeSpan.FromDays(30)));

        agent.Files.Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // A backfill copied in with its original timestamps preserved, which
        // is what the candidate window is right to leave alone and what makes
        // the window cheap.
        agent.Files.Add(
            Garissa, "GARISSA_20260814.dat", agent.Time.GetUtcNow() - TimeSpan.FromDays(7), "a week ago\n");

        await agent.Cycle.RunAsync();

        Assert.Null(agent.Server.Held(11, "GARISSA_20260814.dat"));

        // This is why the button sweeps rather than waiting for the daily one.
        // The person pressing it has almost always just put the files there,
        // and a collect-now that reported "nothing new" to them would be
        // right, useless, and indistinguishable from broken.
        await agent.Cycle.CollectStationAsync(11, ICollectWatcher.Nobody, CancellationToken.None);

        Assert.Equal("a week ago\n", agent.Server.Held(11, "GARISSA_20260814.dat")!.Text);
    }

    [Fact]
    public async Task A_collect_does_not_wipe_the_other_stations_sweep_log()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Garissa, "GARISSA_*.dat"),
                SyncConfigs.Link(12, Kisumu, "KISUMU_*.dat"),
            ]));

        agent.Files
            .Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n")
            .Add(Kisumu, "KISUMU_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();

        // Both stations swept on the first cycle, as every fresh install's
        // first cycle does.
        await agent.Cycle.RunAsync();

        await agent.Cycle.CollectStationAsync(11, ICollectWatcher.Nobody, CancellationToken.None);

        // The sweep log is pruned of stations the device no longer has, and a
        // collect-now's plan knows one station. Pruning on that would empty
        // the log on every press -- so both stations would come up due again,
        // and the next scheduled cycle would offer every folder in full, on
        // the link this product exists for.
        var swept = agent.Store.LoadSweeps().Swept;

        Assert.Equal([11L, 12L], swept.Keys.Order());
    }

    // ---------- one cycle at a time ----------

    [Fact]
    public async Task A_collect_is_refused_while_a_cycle_is_running_rather_than_queued()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));
        agent.Files.Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        // Held open at the manifest, which is where a real cycle spends its
        // time: the folder is walked and the pages are on the wire.
        using var manifested = new SemaphoreSlim(0, 1);

        agent.Server.BeforeManifest = async () => await manifested.WaitAsync();

        var cycle = agent.Cycle.RunAsync();

        await Eventually(() => agent.Cycle.Running);

        var refused = agent.Collects.Start(11);

        Assert.False(refused.Ok);

        // The station is named, so the sentence is followable: it is being
        // collected right now as part of that cycle, and there is nothing
        // else for the person who pressed it to do.
        Assert.Contains("A cycle is already running", refused.Refusal!, StringComparison.Ordinal);
        Assert.Contains("Station 11", refused.Refusal!, StringComparison.Ordinal);

        manifested.Release();

        await cycle;
    }

    [Fact]
    public async Task A_cycle_waits_for_a_collect_rather_than_being_skipped()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));
        agent.Files.Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        // Two manifests pass this gate: the collect's, and the cycle's behind
        // it.
        using var manifested = new SemaphoreSlim(0, 2);

        agent.Server.BeforeManifest = async () => await manifested.WaitAsync();

        var started = agent.Collects.Start(11);

        Assert.True(started.Ok);

        await Eventually(() => agent.Cycle.Running);

        // The scheduled cycle is the thing that must happen; the button
        // merely brings one station's turn forward. A cycle silently dropped
        // because somebody was pressing it is the sort of gap that reaches HQ
        // as a machine that has quietly stopped.
        var cycle = agent.Cycle.RunAsync();

        Assert.False(cycle.IsCompleted);

        manifested.Release();
        manifested.Release();

        await cycle;
    }

    // ---------- the three refusals a person reads ----------

    [Fact]
    public async Task A_station_switched_off_in_ADL_is_refused_with_the_reason()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat", enabled: false));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var refused = agent.Collects.Start(11);

        Assert.False(refused.Ok);

        // HQ's decision, and nothing on this machine to fix. A refusal that
        // did not say so would send a technician hunting for a fault in a
        // folder that is fine.
        Assert.Contains("switched off in ADL", refused.Refusal!, StringComparison.Ordinal);
        Assert.Contains("Nothing on this machine", refused.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_station_with_no_folder_bound_is_refused_with_the_thing_to_do()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, folder: ""));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var refused = agent.Collects.Start(11);

        Assert.False(refused.Ok);

        // The other refusal, wanting the other person: this one is the box on
        // this machine that has not been filled in.
        Assert.Contains("No folder is bound", refused.Refusal!, StringComparison.Ordinal);
        Assert.Contains("Edit settings", refused.Refusal!, StringComparison.Ordinal);
    }

    // ---------- where the result goes ----------

    [Fact]
    public async Task A_collect_does_not_reach_the_heartbeat_as_a_cycle()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.Serving(
            SyncConfigs.Connection(3, "Vaisala AWS", stationLinks:
            [
                SyncConfigs.Link(11, Garissa, "GARISSA_*.dat"),
                SyncConfigs.Link(12, Kisumu, "KISUMU_*.dat"),
            ]));

        agent.Files
            .Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n")
            .Add(Kisumu, "KISUMU_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var cycle = agent.Cycles.LastCompletedCycle;

        Assert.Equal(2, cycle!.Links.Count);

        agent.Time.Advance(TimeSpan.FromMinutes(1));

        await agent.Cycle.CollectStationAsync(11, ICollectWatcher.Nobody, CancellationToken.None);

        // Untouched, and that is the whole decision. Recorded as a cycle, this
        // run would reach HQ as a cycle that had just finished having scanned
        // one station of two -- and ADL's own cycle-stuck and coverage checks
        // would read that as the machine having stopped collecting the rest.
        Assert.Same(cycle, agent.Cycles.LastCompletedCycle);
    }

    [Fact]
    public async Task The_row_shows_the_collect_until_a_cycle_overtakes_it()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));
        agent.Files.Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        agent.Time.Advance(TimeSpan.FromMinutes(1));

        agent.Collects.Start(11);

        await Eventually(() => agent.Collects.Progress is { Running: false });

        var station = Assert.Single(agent.Stations.Read().Stations);

        Assert.NotNull(station.Requested);
        Assert.Equal(1, station.Requested.Scanned);

        // And gone the moment a scheduled cycle covers the same station with
        // fresher numbers. Left there, the row would go on reporting a number
        // from last Tuesday while a cycle five minutes ago said something
        // else.
        agent.Time.Advance(TimeSpan.FromMinutes(1));

        await agent.Cycle.RunAsync();

        Assert.Null(Assert.Single(agent.Stations.Read().Stations).Requested);
    }

    // ---------- watching it, and stopping it ----------

    [Fact]
    public async Task A_run_says_where_it_has_got_to_and_what_it_came_to()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));
        agent.Files.Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var started = agent.Collects.Start(11);

        Assert.True(started.Ok);
        Assert.Equal(11, started.Progress!.StationLinkId);
        Assert.Equal("Station 11", started.Progress.StationName);
        Assert.Equal(Garissa, started.Progress.LocalFolderPath);

        await Eventually(() => agent.Collects.Progress is { Running: false });

        var finished = agent.Collects.Progress!;

        Assert.Equal(1, finished.Scanned);
        Assert.Equal(1, finished.Uploaded);
        Assert.Null(finished.Error);

        // The last run and not nothing, because the window asking is the one
        // that has to show how it ended. A poll landing a moment after the
        // final file would otherwise be told there was no run, on the screen
        // somebody is watching for the answer.
        Assert.NotNull(finished.FinishedAt);
    }

    [Fact]
    public async Task Cancelling_stops_the_run_and_says_it_was_stopped()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));
        agent.Files.Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        using var manifested = new SemaphoreSlim(0, 1);

        agent.Server.BeforeManifest = async () => await manifested.WaitAsync();

        agent.Collects.Start(11);

        await Eventually(() => agent.Cycle.Running);

        Assert.True(agent.Collects.Cancel(11));

        manifested.Release();

        await Eventually(() => agent.Collects.Progress is { Running: false });

        var stopped = agent.Collects.Progress!;

        Assert.True(stopped.Cancelled);
        Assert.Equal("Stopped.", stopped.Step);
    }

    [Fact]
    public async Task Cancelling_names_the_station_so_a_stale_window_cannot_stop_another_run()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));
        agent.Files.Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        using var manifested = new SemaphoreSlim(0, 1);

        agent.Server.BeforeManifest = async () => await manifested.WaitAsync();

        agent.Collects.Start(11);

        await Eventually(() => agent.Cycle.Running);

        // A window somebody left open on another station. Stopping "the one
        // running" would let it stop a run it is not the window for.
        Assert.False(agent.Collects.Cancel(99));

        manifested.Release();

        await Eventually(() => agent.Collects.Progress is { Running: false });
    }

    /// <summary>Wait for something the run does on a thread of its own.</summary>
    /// <remarks>
    /// The run is deliberately started and not awaited -- the control surface
    /// serves one client at a time, and a command that waited for an upload
    /// would freeze the tray's own status poll for its duration -- so a test
    /// about it has to wait for it the way the window does.
    /// </remarks>
    private static async Task Eventually(Func<bool> settled)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (settled())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The run never reached the state this test is about.");
    }

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
}
