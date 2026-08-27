using AdlAgent.Core.Api;
using AdlAgent.Core.Cycle;
using AdlAgent.Core.Diagnostics;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// What a unit pass leaves behind it, and whether it is still there tomorrow.
/// </summary>
/// <remarks>
/// The problem this answers, in one sentence: nothing the agent did survived
/// the cycle that did it. The counts lived in memory for the heartbeat and
/// were overwritten ten minutes later; the sentence that says why a silent
/// station is silent was computed and thrown away. When Comoros asks what
/// happened at 13:24, this is the only thing on the machine that can answer.
/// </remarks>
public class CycleLogTests
{
    private const string Folder = "C:\\VendorData\\Garissa";

    [Fact]
    public async Task A_pass_is_written_down_with_what_it_walked_and_what_it_sent()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        agent.Files
            .Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n")
            .Add(Folder, "GARISSA_20260820.dat", Settled(agent), "09:00,20.9\n")
            // Another vendor's file in the same folder, which no station here
            // claims. In every count this product has, it is invisible.
            .Add(Folder, "MOMBASA_20260821.dat", Settled(agent), "09:00,29.1\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var pass = Assert.Single(await agent.RecordedPassesAsync());

        Assert.Equal(Folder, pass.Unit);
        Assert.True(pass.Completed);

        var folder = Assert.Single(pass.Folders);

        Assert.Equal(Folder, folder.Folder);
        Assert.Equal(3, folder.Entries);

        var station = Assert.Single(pass.Stations);

        Assert.Equal(11, station.StationLinkId);
        Assert.Equal("Station 11", station.Station);
        Assert.Equal(2, station.Scanned);
        Assert.Equal(2, station.Offered);
        Assert.Equal(2, station.Uploaded);
        Assert.Equal(0, station.Failed);

        Assert.Equal(
            ["GARISSA_20260820.dat", "GARISSA_20260821.dat"],
            pass.Files
                .Where(file => file.Outcome == FileOutcomes.Uploaded)
                .Select(file => file.Name!)
                .Order());

        // And the file nobody claimed, which is how a vendor that started
        // writing .DAT into a folder configured for .dat is told apart from a
        // folder that is empty.
        var stray = Assert.Single(pass.Files, file => file.Outcome == FileOutcomes.Unmatched);

        Assert.Equal("MOMBASA_20260821.dat", stray.Name);
    }

    [Fact]
    public async Task A_station_the_scan_turned_away_is_recorded_with_the_reason()
    {
        await using var agent = new AgentHarness();

        // Half-configured: a folder and no pattern. A common real fault, and
        // before this it was invisible everywhere -- the station simply did
        // nothing, cycle after cycle.
        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, pattern: ""));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var pass = Assert.Single(await agent.RecordedPassesAsync());
        var station = Assert.Single(pass.Stations);

        Assert.Equal(0, station.Scanned);
        Assert.Contains("No file pattern", station.Error!);

        // It walked nothing, and says so rather than claiming a folder it
        // never opened.
        Assert.Empty(pass.Folders);
    }

    [Fact]
    public async Task A_pass_cut_short_is_written_down_with_what_it_got_to_and_why_it_stopped()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));
        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

        await agent.PairAsync();

        // One good pass first, so the machine has a configuration to work
        // from. Without one there is no cycle at all, and a record of a cycle
        // that never happened would be worse than the silence.
        await agent.Cycle.RunAsync();

        agent.Time.Advance(TimeSpan.FromMinutes(10));

        // And then ADL goes.
        agent.Server.Unreachable = true;

        await agent.Cycle.RunAsync();

        var passes = await agent.RecordedPassesAsync();
        var pass = passes[0];

        Assert.False(pass.Completed);
        Assert.NotNull(pass.Stopped);

        // What it got to still stands, which is the whole reason a cut-short
        // pass is written down at all.
        Assert.Equal(1, Assert.Single(pass.Stations).Scanned);
    }

    [Fact]
    public async Task Five_hundred_identical_failures_are_one_line_and_a_count()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));

        for (var index = 0; index < 40; index++)
        {
            agent.Files.Add(
                Folder,
                $"GARISSA_{index:D4}.dat",
                Settled(agent) - TimeSpan.FromMinutes(index),
                $"{index}\n");

            agent.Server.RefusedUploads.Add($"GARISSA_{index:D4}.dat");
        }

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var pass = Assert.Single(await agent.RecordedPassesAsync());
        var failures = pass.Files.Where(file => file.Outcome == FileOutcomes.Failed).ToList();

        // The whole of why the bound is by usefulness rather than by a count:
        // forty files refused for one reason is one line and a number, and the
        // one interesting anomaly among them would be the line beside it.
        var folded = Assert.Single(failures);

        Assert.Equal(40, folded.Count);
        Assert.NotNull(folded.Name);
        Assert.Contains("does not hash", folded.Reason!);
    }

    [Fact]
    public async Task A_pass_that_uploads_hundreds_keeps_a_sample_and_a_tally()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));

        const int many = UnitJournal.UploadedSample * 3;

        for (var index = 0; index < many; index++)
        {
            agent.Files.Add(
                Folder,
                $"GARISSA_{index:D4}.dat",
                Settled(agent) - TimeSpan.FromMinutes(index),
                $"{index}\n");
        }

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var pass = Assert.Single(await agent.RecordedPassesAsync());
        var uploaded = pass.Files.Where(file => file.Outcome == FileOutcomes.Uploaded).ToList();

        Assert.Equal(UnitJournal.UploadedSample, uploaded.Count(file => file.Name is not null));

        // And the rest as a number, so a catastrophic pass is no longer than a
        // quiet one and still says the right thing.
        var remainder = Assert.Single(uploaded, file => file.Name is null);

        Assert.Equal(many - UnitJournal.UploadedSample, remainder.Count);
    }

    [Fact]
    public async Task A_file_still_being_written_says_how_recently_and_against_what_window()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));

        // Written twelve seconds ago, inside the station's sixty-second
        // stability window: the state the newest file in a live folder is in
        // on every single cycle.
        agent.Files.Add(
            Folder,
            "GARISSA_20260821.dat",
            agent.Time.GetUtcNow() - TimeSpan.FromSeconds(12),
            "09:00,21.4\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var pass = Assert.Single(await agent.RecordedPassesAsync());
        var held = Assert.Single(pass.Files, file => file.Outcome == FileOutcomes.Held);

        Assert.Equal("GARISSA_20260821.dat", held.Name);
        Assert.Contains("window 60s", held.Reason!);

        Assert.Equal(1, Assert.Single(pass.Stations).Held);
    }

    [Fact]
    public async Task A_sweep_is_recorded_as_a_sweep_and_a_tick_as_a_tick()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs
            .With(SyncConfigs.Link(11, Folder, "*.dat"))
            .ReconcilingEvery(24);

        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

        await agent.PairAsync();

        // The first pass a station has ever had is its first sweep.
        await agent.Cycle.RunAsync();

        agent.Time.Advance(TimeSpan.FromMinutes(10));

        await agent.Cycle.RunAsync();

        var passes = await agent.RecordedPassesAsync();

        Assert.Equal(2, passes.Count);
        Assert.Equal(CycleTriggers.Scheduled, passes[0].Trigger);
        Assert.Equal(CycleTriggers.Reconciliation, passes[1].Trigger);
    }

    [Fact]
    public async Task A_collect_somebody_asked_for_is_recorded_as_one()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));
        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

        await agent.PairAsync();

        var run = await agent.Cycle.CollectStationAsync(
            11, ICollectWatcher.Nobody, CancellationToken.None);

        Assert.NotNull(run);

        var pass = Assert.Single(await agent.RecordedPassesAsync());

        Assert.Equal(CycleTriggers.Collect, pass.Trigger);
        Assert.Equal(1, Assert.Single(pass.Stations).Uploaded);
    }

    [Fact]
    public async Task Every_pass_carries_the_ADL_it_was_made_against()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var pass = Assert.Single(await agent.RecordedPassesAsync());

        // A repoint deliberately leaves this folder alone, which is what makes
        // this necessary: without it, station link ids issued by an instance
        // this machine no longer talks to would read as current.
        Assert.Contains(agent.Server.BaseAddress.Host, pass.Instance);
    }

    [Fact]
    public async Task The_record_is_still_there_after_the_service_is_restarted()
    {
        var store = new InMemoryAgentStateStore();

        // The test's own state directory rather than the harness's, which the
        // harness deletes on the way out -- and the point of this test is what
        // is on the disk after the process that wrote it has gone.
        var state = Directory.CreateTempSubdirectory("adl-agent-restart").FullName;

        await using (var agent = new AgentHarness(
            store,
            new Dictionary<string, string?> { ["Agent:StateDirectory"] = state }))
        {
            agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));
            agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

            await agent.PairAsync();
            await agent.Cycle.RunAsync();
            await agent.CycleLog.FlushAsync();
        }

        // The whole point. In memory, the honest answer after a restart is
        // "nothing since I started"; on the disk, it is what happened.
        var read = new CycleLogReader(AgentLogs.In(state)).Recent(new CyclePassQuery(Most: 10));

        Assert.Single(read);
        Assert.Equal(Folder, read[0].Unit);

        Directory.Delete(state, recursive: true);
    }

    [Fact]
    public async Task A_station_can_be_asked_for_its_own_passes()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, Folder, "*.dat"),
            SyncConfigs.Link(12, "C:\\VendorData\\Kisumu", "*.dat"));

        agent.Files
            .Add(Folder, "GARISSA_20260821.dat", Settled(agent), "a\n")
            .Add("C:\\VendorData\\Kisumu", "KISUMU_20260821.dat", Settled(agent), "b\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // Two folders, so two units, so two records.
        Assert.Equal(2, (await agent.RecordedPassesAsync()).Count);

        var kisumu = Assert.Single(await agent.RecordedPassesAsync(stationLinkId: 12));

        Assert.Equal("C:\\VendorData\\Kisumu", kisumu.Unit);
    }

    [Fact]
    public async Task The_logs_are_below_the_state_folder_and_never_in_it()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();
        await agent.CycleLog.FlushAsync();

        // The state folder holds the device token, the configuration cache and
        // the sweep log, and an eviction routine that walked it would be one
        // deletion away from a machine somebody has to visit.
        Assert.Equal(
            Path.Combine(agent.HostLifecycle.StateDirectory, AgentLogs.FolderName),
            agent.LogDirectory);

        Assert.NotEmpty(Directory.EnumerateFiles(agent.LogDirectory));
    }

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
}
