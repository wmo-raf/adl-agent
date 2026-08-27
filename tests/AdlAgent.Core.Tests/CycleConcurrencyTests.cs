using AdlAgent.Core.Api;
using AdlAgent.Core.Cycle;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// How much of a machine's collection happens at once, and what bounds it.
/// </summary>
/// <remarks>
/// Everywhere else in this suite concurrency is turned down to one, so that
/// the sequences those tests pinned still mean what they meant. Here it is
/// the subject, so these assert only what is true whatever the order: that
/// the bounds hold, that ADL's number is the one obeyed, and that a unit
/// finding ADL gone takes the rest of the tick with it.
/// </remarks>
public class CycleConcurrencyTests
{
    private const string Garissa = "C:\\VendorData\\Garissa";

    [Fact]
    public void ADL_sets_the_upload_bound()
    {
        var concurrency = new CycleConcurrency();

        Assert.Equal(6, concurrency.UploadsFor(new AgentLimits { ConcurrentUploads = 6 }));
    }

    [Fact]
    public void An_ADL_that_says_nothing_gets_the_default_reading()
    {
        // An instance that predates the field sends no number at all, and the
        // limits record's own default is what that silence means -- the same
        // arrangement the reconciliation interval and the dated-folder window
        // already have.
        Assert.Equal(4, new CycleConcurrency().UploadsFor(new AgentLimits()));
    }

    [Fact]
    public void A_number_that_could_not_be_a_bound_is_clamped_rather_than_obeyed()
    {
        var concurrency = new CycleConcurrency { MostUploads = 8 };

        // Zero would be a machine that never uploads anything again, and a
        // thousand would be a thousand sockets on a link chosen for being the
        // only one a country has. Neither is a number to take literally from
        // an admin field somebody typed into.
        Assert.Equal(1, concurrency.UploadsFor(new AgentLimits { ConcurrentUploads = 0 }));
        Assert.Equal(8, concurrency.UploadsFor(new AgentLimits { ConcurrentUploads = 1000 }));
    }

    [Fact]
    public async Task No_more_uploads_are_in_flight_than_ADL_allows()
    {
        await using var agent = new AgentHarness(
            concurrency: new CycleConcurrency { Units = 4, MostUploads = 32 });

        agent.Server.AnswersConcurrently = true;
        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));
        agent.Server.Config = agent.Server.Config with
        {
            Limits = new AgentLimits { ConcurrentUploads = 2 },
        };

        for (var index = 0; index < 12; index++)
        {
            agent.Files.Add(Garissa, $"GARISSA_{index:D4}.dat", Settled(agent), $"{index}\n");
        }

        var inFlight = 0;
        var most = 0;

        agent.Server.BeforeUpload = async () =>
        {
            var now = Interlocked.Increment(ref inFlight);

            // Watched at its peak rather than sampled at the end, which would
            // only ever read zero.
            InterlockedMax(ref most, now);

            // Long enough that a machine ignoring the bound would have every
            // file on the wire at once and be caught for it.
            await Task.Delay(30);

            Interlocked.Decrement(ref inFlight);
        };

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal(12, agent.Server.Ledger.Count);

        // Exactly two, which asserts both halves at once: the uploads really
        // do overlap -- twelve files at one round trip each is the difference
        // between a station catching up this morning and this week -- and
        // they stop at the number ADL gave. A range would have passed on a
        // machine that never parallelised anything at all.
        Assert.Equal(2, Volatile.Read(ref most));
    }

    [Fact]
    public async Task A_unit_finding_ADL_gone_stops_the_rest_of_the_tick()
    {
        await using var agent = new AgentHarness(
            concurrency: new CycleConcurrency { Units = 1, MostUploads = 1 });

        // Twelve stations, twelve units, each with a file to offer.
        agent.Server.Config = SyncConfigs.With(
            Enumerable.Range(11, 12)
                .Select(id => SyncConfigs.Link(id, $"{Garissa}{id}", "*.dat"))
                .ToArray());

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        for (var id = 11; id < 23; id++)
        {
            agent.Files.Add($"{Garissa}{id}", "GARISSA.dat", Settled(agent), "today\n");
        }

        agent.Server.Unreachable = true;

        await agent.Cycle.RunAsync();

        // The first unit to find the link down ends the tick. The rest are
        // never started, so they report nothing -- reporting a pass that
        // never happened would be worse than the silence, and hammering a
        // server already down would be worse than both.
        //
        // The reports still standing are last tick's, when every station was
        // scanned and found nothing. Exactly one has been overwritten, by the
        // unit that ran into the wall.
        var scanned = agent.Cycles.LastCompletedCycle!.Links.Count(link => link.Scanned > 0);

        Assert.Equal(1, scanned);
    }

    [Fact]
    public async Task The_window_says_a_machine_is_collecting_while_it_is()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));
        agent.Files.Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        // Nothing running: the header has nothing to say, and says nothing
        // rather than "collecting 0 stations".
        Assert.False(agent.Status.Read().CollectingStations > 0);

        using var manifested = new SemaphoreSlim(0, 1);

        agent.Server.BeforeManifest = async () => await manifested.WaitAsync();

        var tick = agent.Cycle.RunAsync();

        await Eventually(() => agent.Status.Read().CollectingStations == 1);

        // And the row says so too, rather than showing the counts of a pass
        // that has been superseded by the one running now.
        var station = Assert.Single(agent.Stations.Read().Stations);

        Assert.True(station.Collecting);

        manifested.Release();

        await tick;

        Assert.Equal(0, agent.Status.Read().CollectingStations);
        Assert.False(Assert.Single(agent.Stations.Read().Stations).Collecting);
    }

    [Fact]
    public async Task A_collect_is_refused_only_for_the_station_that_is_busy()
    {
        await using var agent = new AgentHarness();

        // Two stations, two folders, two units.
        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, Garissa, "*.dat"),
            SyncConfigs.Link(12, $"{Garissa}2", "*.dat"));

        agent.Files.Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        using var manifested = new SemaphoreSlim(0, 1);

        agent.Server.BeforeManifest = async () => await manifested.WaitAsync();

        var tick = agent.Cycle.RunAsync();

        await Eventually(() => agent.Cycle.IsCollecting(11));

        // The whole of why the gate stopped being one gate on the machine.
        // Station 12 has nothing to do with the folder being collected, and a
        // technician standing at this machine can still ask for it.
        Assert.False(agent.Cycle.IsCollecting(12));

        manifested.Release();

        await tick;
    }

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

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;

        while (value > (seen = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen)
            {
                return;
            }
        }
    }

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
}
