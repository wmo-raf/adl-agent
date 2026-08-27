using AdlAgent.Core.Api;
using AdlAgent.Core.Diagnostics;
using AdlAgent.Core.Heartbeat;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Story 23 and wmo-raf/adl#307: what this machine has been doing reaches
/// ADL, not just what it did in the last five minutes.
/// </summary>
/// <remarks>
/// ADL kept exactly one cycle's worth of agent history and overwrote it every
/// beat. These pin the other half: every pass that finishes goes on a bounded
/// queue, the beat drains it, and a beat ADL refuses costs nothing -- while
/// the rolling picture the liveness ladder is counted in goes on travelling
/// beside it, unchanged.
/// </remarks>
public class CollectionHistoryTests
{
    private const string Folder = "C:\\VendorData\\Garissa";

    [Fact]
    public async Task A_beat_carries_the_passes_that_finished_since_the_last_one()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        agent.Files
            .Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n")
            .Add(Folder, "GARISSA_20260820.dat", Settled(agent), "09:00,20.9\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();
        await agent.HeartbeatLoop.BeatAsync();

        var beat = Assert.Single(agent.Server.Heartbeats);
        var pass = Assert.Single(beat.CompletedPasses);

        Assert.Equal(Folder, pass.Unit);
        Assert.True(pass.Completed);

        // A sweep, because the first pass a fresh machine makes offers
        // everything back to the collection start date rather than only the
        // candidate window. The trigger travels so that ADL can tell that
        // apart from an ordinary tick -- a sweep scanning a year of folders
        // and an ordinary cycle scanning one are not the same event.
        Assert.Equal(CycleTriggers.Reconciliation, pass.Trigger);
        Assert.Null(pass.Stopped);
        Assert.Equal(1, pass.Folders);

        var station = Assert.Single(pass.Stations);

        Assert.Equal(11, station.StationLinkId);
        Assert.Equal(2, station.Scanned);
        Assert.Equal(2, station.Offered);
        Assert.Equal(2, station.Uploaded);
        Assert.Equal(0, station.Failed);
        Assert.Equal(0, station.Backlog);
    }

    /// <summary>
    /// The regression this issue is most at risk of causing, stated as a
    /// test: an agent that stopped sending <c>last_cycle</c> would make every
    /// auto-updated machine read as cycle-stuck to every ADL not yet
    /// upgraded.
    /// </summary>
    [Fact]
    public async Task The_rolling_picture_still_travels_beside_the_passes()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));
        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();
        await agent.HeartbeatLoop.BeatAsync();

        var beat = Assert.Single(agent.Server.Heartbeats);

        Assert.NotNull(beat.LastCycle);
        Assert.NotNull(beat.LastCycle!.CompletedAt);

        var link = Assert.Single(beat.LastCycle.Links);

        Assert.Equal(11, link.StationLinkId);
        Assert.Equal(1, link.Uploaded);

        // And the same pass, in the shape that becomes history.
        Assert.Single(beat.CompletedPasses);
    }

    [Fact]
    public async Task A_beat_ADL_refuses_leaves_its_passes_for_the_next_one()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));
        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        agent.Server.Unreachable = true;

        await agent.HeartbeatLoop.BeatAsync();

        Assert.Empty(agent.Server.Heartbeats);

        agent.Server.Unreachable = false;

        await agent.HeartbeatLoop.BeatAsync();

        var beat = Assert.Single(agent.Server.Heartbeats);

        Assert.Single(beat.CompletedPasses);

        // And the pass is not sent twice: the beat that carried it is the
        // beat that let it go.
        await agent.HeartbeatLoop.BeatAsync();

        Assert.Empty(agent.Server.Heartbeats[1].CompletedPasses);
    }

    [Fact]
    public async Task A_beat_ADL_refuses_costs_the_machine_nothing_it_collected()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));
        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        agent.Server.Unreachable = true;
        await agent.HeartbeatLoop.BeatAsync();
        agent.Server.Unreachable = false;

        // The pass is still on the disk, whatever ADL did with the beat, and
        // the machine went on collecting.
        var pass = Assert.Single(await agent.RecordedPassesAsync());

        Assert.Equal(Folder, pass.Unit);
        Assert.True(pass.Completed);
    }

    /// <summary>
    /// An empty list, not an absent field -- which is the whole of how ADL
    /// tells a new agent with nothing to say from an old one that has never
    /// heard of passes.
    /// </summary>
    /// <remarks>
    /// The old one gets its <c>last_cycle</c> read as one pass per beat. Do
    /// that to a new agent between cycles and every quiet beat would write a
    /// duplicate row of the same cycle, for ever.
    /// </remarks>
    [Fact]
    public async Task A_beat_with_no_passes_says_so_rather_than_leaving_it_out()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();
        await agent.HeartbeatLoop.BeatAsync();

        var sent = Assert.Single(agent.Server.RequestsFor("heartbeat/"));

        Assert.Contains("\"completed_passes\":[]", sent.Body, StringComparison.Ordinal);

        // And nothing about shedding, on the beat of a machine that has shed
        // nothing: a zero every five minutes is a field ADL learns to read
        // past, and this one is meant to be noticed.
        Assert.DoesNotContain("dropped_passes", sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_outage_sheds_the_oldest_passes_and_says_how_many()
    {
        var store = new CycleReportStore();

        for (var made = 0; made < CycleReportStore.Capacity + 3; made++)
        {
            store.Enqueue(Pass($"C:\\VendorData\\Unit{made}"));
        }

        var batch = store.Peek(CycleReportStore.Capacity);

        Assert.Equal(3, batch.Dropped);

        // The oldest went, not the newest: the newest is what says how the
        // machine is now.
        Assert.Equal("C:\\VendorData\\Unit3", batch.Passes[0].Unit);
        Assert.Equal(CycleReportStore.Capacity, batch.Passes.Count);

        store.Delivered(batch);

        Assert.Equal(0, store.Peek().Dropped);
    }

    /// <summary>
    /// The batch is settled by position, not by count -- which matters
    /// because the cycle goes on finishing units while a beat is in flight.
    /// </summary>
    /// <remarks>
    /// On a machine whose queue is full, the passes a beat was built from can
    /// be shed to make room while ADL is still answering. Dropping "however
    /// many were sent" off the head would then throw away passes nobody had
    /// sent anywhere -- silently, in exactly the long outage the shedding
    /// exists for.
    /// </remarks>
    [Fact]
    public void Shedding_while_a_beat_is_in_flight_costs_no_undelivered_pass()
    {
        var store = new CycleReportStore();

        for (var made = 0; made < CycleReportStore.Capacity; made++)
        {
            store.Enqueue(Pass($"C:\\VendorData\\Unit{made}"));
        }

        var batch = store.Peek(10);

        // The link is slow, and three more units finish while the beat is on
        // the wire. The queue is full, so the three oldest go -- and those
        // three are in the batch.
        for (var made = 0; made < 3; made++)
        {
            store.Enqueue(Pass($"C:\\VendorData\\Late{made}"));
        }

        store.Delivered(batch);

        var left = store.Peek(CycleReportStore.Capacity);

        // The seven of the batch that were still here have gone, and nothing
        // beyond them: the queue picks up at the eleventh pass. Three shed
        // and seven delivered out of a queue that had taken three more, so
        // what is left is the ceiling less the seven.
        Assert.Equal("C:\\VendorData\\Unit10", left.Passes[0].Unit);
        Assert.Equal(CycleReportStore.Capacity - 7, left.Passes.Count);
        Assert.Equal("C:\\VendorData\\Late2", left.Passes[^1].Unit);

        // The three shed are still owed as a gap: this beat reported none.
        Assert.Equal(3, left.Dropped);
    }

    [Fact]
    public void A_beat_carries_at_most_one_batch_of_passes()
    {
        var store = new CycleReportStore();

        for (var made = 0; made < CycleReportStore.PerBeat + 5; made++)
        {
            store.Enqueue(Pass($"C:\\VendorData\\Unit{made}"));
        }

        var first = store.Peek();

        Assert.Equal(CycleReportStore.PerBeat, first.Passes.Count);

        store.Delivered(first);

        Assert.Equal(5, store.Peek().Passes.Count);
    }

    /// <summary>
    /// The point of the whole field: the names of files that were seen and
    /// did not arrive.
    /// </summary>
    /// <remarks>
    /// ADL already stores the name of every file it received. This is the
    /// negative space -- and the reason the three slots are shared out a
    /// round at a time rather than in priority order: a pass with forty
    /// failures in it still spends one on the unmatched name, which is the
    /// only line anywhere that says a vendor has renamed its files.
    /// </remarks>
    [Fact]
    public void A_pass_names_a_few_of_the_files_that_did_not_arrive()
    {
        var pass = PassReports.Of(Pass(Folder) with
        {
            Files =
            [
                new CycleFileRecord
                {
                    Outcome = FileOutcomes.Uploaded, Name = "GARISSA_20260821.dat", Count = 1,
                },
                new CycleFileRecord
                {
                    Outcome = FileOutcomes.Failed,
                    Name = "GARISSA_20260819.dat",
                    StationLinkId = 11,
                    Reason = "The share stopped answering.",
                    Count = 40,
                },
                new CycleFileRecord
                {
                    Outcome = FileOutcomes.Failed,
                    Name = "GARISSA_20260818.dat",
                    StationLinkId = 11,
                    Reason = "ADL refused the name.",
                    Count = 1,
                },
                new CycleFileRecord
                {
                    Outcome = FileOutcomes.Unmatched, Name = "GARISSA_20260821.DAT", Count = 1,
                },
                new CycleFileRecord
                {
                    Outcome = FileOutcomes.Held,
                    Name = "GARISSA_20260822.dat",
                    StationLinkId = 11,
                    Reason = "Still being written.",
                    Count = 1,
                },
            ],
        });

        Assert.Equal(PassReports.MostMissing, pass.Missing.Count);

        Assert.Equal(
            [FileOutcomes.Failed, FileOutcomes.Unmatched, FileOutcomes.Held],
            pass.Missing.Select(file => file.Outcome));

        Assert.Equal("GARISSA_20260819.dat", pass.Missing[0].Name);
        Assert.Equal("The share stopped answering.", pass.Missing[0].Reason);
        Assert.Equal(11, pass.Missing[0].StationLinkId);

        // No station: that is what an unmatched file is, and what makes it
        // invisible to every other number in this product.
        Assert.Null(pass.Missing[1].StationLinkId);

        // Uploads are not "missing", and neither is anything the pass did not
        // name.
        Assert.DoesNotContain(
            pass.Missing, file => file.Outcome == FileOutcomes.Uploaded);
    }

    [Fact]
    public void A_pass_that_lost_no_files_names_none()
    {
        var pass = PassReports.Of(Pass(Folder) with
        {
            Files =
            [
                new CycleFileRecord
                {
                    Outcome = FileOutcomes.Uploaded, Name = "GARISSA_20260821.dat", Count = 1,
                },
                // A pure tally, which the journal writes when it had no room
                // to name what it was counting. The shape of an answer
                // without being one.
                new CycleFileRecord { Outcome = FileOutcomes.Failed, Count = 12 },
            ],
        });

        Assert.Empty(pass.Missing);
    }

    /// <summary>
    /// A collect somebody pressed at the machine is a pass like any other,
    /// and reaches ADL marked as the button it was.
    /// </summary>
    [Fact]
    public async Task A_collect_somebody_asked_for_reaches_ADL_as_one()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));
        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        // Started and not awaited, because that is how the button works: the
        // control surface serves one client at a time and a command that
        // waited for an upload would freeze the tray's own status poll.
        Assert.True(agent.Collects.Start(11).Ok);

        await Eventually(() => agent.Collects.Progress is { Running: false });

        await agent.HeartbeatLoop.BeatAsync();

        var beat = Assert.Single(agent.Server.Heartbeats);
        var pass = Assert.Single(beat.CompletedPasses);

        Assert.Equal(CycleTriggers.Collect, pass.Trigger);
    }

    /// <summary>Wait for something the collect does on a thread of its own.</summary>
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

        Assert.Fail("The collect never reached the state this test is about.");
    }

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);

    private static CycleRecord Pass(string unit) =>
        new()
        {
            At = TestClock.Start,
            Seconds = 1.5,
            Unit = unit,
            Trigger = CycleTriggers.Scheduled,
            Completed = true,
            Folders = [new CycleFolderRecord(unit, 3)],
            Stations = [],
            Files = [],
        };
}
