using System.Diagnostics;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Story 17: the folder where listing is itself the problem.
/// </summary>
/// <remarks>
/// One directory, a minute-by-minute file for every station in the country,
/// years of them. Everything the enumerate strategy does well is beside the
/// point there -- the walk is the cost, and no amount of not-hashing helps.
/// A station ADL puts on DIRECT_FETCH stops asking what is in the folder and
/// starts asking about the names its vendor's clock implies, one at a time.
/// </remarks>
public class DirectFetchTests
{
    private const string Folder = "C:\\VendorData\\All";

    [Fact]
    public async Task A_direct_fetch_station_never_lists_its_folder()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.DirectFetchLink(11, Folder, watermark: Watermark(agent, minutes: 30)));

        // Two of the four names this station expects between the watermark
        // and now. The clock is 09:00 UTC, which is noon in Nairobi.
        agent.Files
            .Add(Folder, "GARISSA_202608211140.dat", Settled(agent), "11:40,21.4\n")
            .Add(Folder, "GARISSA_202608211130.dat", Settled(agent), "11:30,21.2\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // The whole point, and the only assertion that can make it: not one
        // listing, however many files are in there.
        Assert.Equal(0, agent.Files.EnumerationsOf(Folder));
        Assert.Equal(4, agent.Files.DescribesOf(Folder));

        Assert.Equal("11:40,21.4\n", agent.Server.Held(11, "GARISSA_202608211140.dat")!.Text);
        Assert.Equal("11:30,21.2\n", agent.Server.Held(11, "GARISSA_202608211130.dat")!.Text);
    }

    [Fact]
    public async Task An_expected_file_that_is_not_there_is_not_an_event()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.DirectFetchLink(11, Folder, watermark: Watermark(agent, minutes: 30)));

        agent.Files.Add(Folder, "GARISSA_202608211140.dat", Settled(agent), "11:40,21.4\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        // Three of the four expected names were missing, and one of them --
        // the interval that has not finished yet -- will be missing on every
        // cycle for the life of the install. A station reporting three
        // failures every ten minutes for ever is a station nobody looks at.
        Assert.Equal(1, link.Scanned);
        Assert.Equal(1, link.Uploaded);
        Assert.Equal(0, link.Failed);
        Assert.Null(link.Error);
    }

    [Fact]
    public async Task A_station_that_finds_none_of_its_names_says_so()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.DirectFetchLink(11, Folder, watermark: Watermark(agent, minutes: 30)));

        // The vendor's real prefix is GARISSA_ and this station was given
        // GARISA_ by someone typing quickly. Nothing is found, and unlike a
        // single missing file that is worth a sentence: it is the only way a
        // technician learns that the name being built is not the name on the
        // disk.
        agent.Files.Add(Folder, "GARISSA_202608211140.dat", Settled(agent), "11:40,21.4\n");

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.DirectFetchLink(
                11, Folder, prefix: "GARISA_", watermark: Watermark(agent, minutes: 30)));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Contains("None of the 4 filenames", link.Error!);
        Assert.Contains(Folder, link.Error!);
        Assert.Empty(agent.Server.ManifestPages);
    }

    [Theory]
    [InlineData(null, "yyyyMMddHHmm", "No file interval")]
    [InlineData(10, null, "No filename datetime format")]
    public async Task A_station_ADL_has_left_half_configured_says_which_half(
        int? interval, string? format, string expected)
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.DirectFetchLink(
                11, Folder, intervalMinutes: interval, datetimeFormat: format));

        agent.Files.Add(Folder, "GARISSA_202608211140.dat", Settled(agent), "11:40,21.4\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Contains(expected, link.Error!);

        // And it does not fall back to walking the folder. The folder a
        // station was put on Direct Fetch for is the one that must never be
        // walked by accident.
        Assert.Equal(0, agent.Files.EnumerationsOf(Folder));
        Assert.Empty(agent.Server.ManifestPages);
    }

    [Fact]
    public async Task A_backfill_beyond_a_cycle_s_reach_is_found_by_the_daily_reconciliation()
    {
        await using var agent = new AgentHarness();

        // A minute cadence with two months behind it: an ordinary cycle looks
        // for the newest twenty thousand names, which is a fortnight of them,
        // and stops. ADL's floor never moves to bring the rest closer, so
        // without the daily deep pass a file older than that fortnight would
        // be looked for on no cycle, ever -- which is story 15 quietly
        // failing for every station on this strategy.
        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.DirectFetchLink(
                11,
                Folder,
                intervalMinutes: 1,
                datetimeFormat: "yyyyMMddHHmm",
                watermark: agent.Time.GetUtcNow() - TimeSpan.FromDays(60)));

        agent.Files.Add(Folder, "GARISSA_202608211159.dat", Settled(agent), "11:59,21.4\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // Recovered off a dead logger and copied in a month late. Its name
        // says the twenty-second of July, which is thirty days back.
        agent.Files.Add(Folder, "GARISSA_202607221159.dat", Settled(agent), "22 July\n");

        await agent.Cycle.RunAsync();

        Assert.Null(agent.Server.Held(11, "GARISSA_202607221159.dat"));

        agent.Time.Advance(TimeSpan.FromHours(25));

        await agent.Cycle.RunAsync();

        Assert.Equal("22 July\n", agent.Server.Held(11, "GARISSA_202607221159.dat")!.Text);

        // And still without ever listing the folder, which is the whole
        // reason this station is on this strategy.
        Assert.Equal(0, agent.Files.EnumerationsOf(Folder));
    }

    [Fact]
    public async Task A_station_out_of_reach_of_even_a_reconciliation_says_so()
    {
        await using var agent = new AgentHarness();

        // A minute cadence and a collection start date two years back is a
        // million names. The deep pass reaches half of them; the rest is
        // genuinely out of reach, and that is the one case worth telling an
        // operator to act on.
        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.DirectFetchLink(
                11,
                Folder,
                intervalMinutes: 1,
                datetimeFormat: "yyyyMMddHHmm",
                watermark: agent.Time.GetUtcNow() - TimeSpan.FromDays(730)));

        agent.Files.Add(Folder, "GARISSA_202608211159.dat", Settled(agent), "11:59,21.4\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Contains("out of reach", link.Error!);

        // Cut off at the far end, not this one: today's file still went.
        Assert.Equal("11:59,21.4\n", agent.Server.Held(11, "GARISSA_202608211159.dat")!.Text);
    }

    [Fact]
    public async Task A_million_files_in_the_folder_cost_nothing_because_nothing_reads_them()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.DirectFetchLink(11, Folder, watermark: Watermark(agent, minutes: 30)));

        // The pathological folder itself: a minute-by-minute file for every
        // station in the country, stated but never written. Under ENUMERATE
        // this is a walk over a million entries on every cycle; here it is
        // four questions with names in them, and the million might as well
        // not be there. Which is the assertion -- if any of these were
        // opened, they would not be found.
        var ancient = agent.Time.GetUtcNow() - TimeSpan.FromDays(400);

        for (var index = 0; index < 1_000_000; index++)
        {
            agent.Files.State(Folder, $"OTHER_{index:0000000}.dat", ancient.AddMinutes(index), length: 512);
        }

        agent.Files.Add(Folder, "GARISSA_202608211140.dat", Settled(agent), "11:40,21.4\n");

        await agent.PairAsync();

        var clock = Stopwatch.StartNew();

        await agent.Cycle.RunAsync();

        clock.Stop();

        Assert.Equal(0, agent.Files.EnumerationsOf(Folder));
        Assert.Equal(4, agent.Files.DescribesOf(Folder));
        Assert.Equal("11:40,21.4\n", agent.Server.Held(11, "GARISSA_202608211140.dat")!.Text);

        // Wildly generous -- the cycle here is four stat calls, one manifest
        // and one upload over loopback -- and still far below what a walk
        // over a million entries would cost.
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(10),
            $"A direct-fetch cycle over a million-file folder took {clock.Elapsed}.");
    }

    [Fact]
    public async Task A_station_ADL_moves_onto_direct_fetch_stops_walking_from_that_cycle()
    {
        await using var agent = new AgentHarness();

        var watermark = Watermark(agent, minutes: 30);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat", watermark));

        agent.Files
            .Add(Folder, "GARISSA_202608211140.dat", Settled(agent), "11:40,21.4\n")
            // A name the clock never implies. Under the pattern it is this
            // station's; under Direct Fetch it does not exist.
            .Add(Folder, "GARISSA_yesterday.dat", Settled(agent), "who knows\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal(1, agent.Files.EnumerationsOf(Folder));
        Assert.NotNull(agent.Server.Held(11, "GARISSA_yesterday.dat"));

        // The folder turned out to be one of the bad ones and an
        // administrator moved the station over.
        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.DirectFetchLink(11, Folder, watermark: watermark));

        agent.Files.Add(Folder, "GARISSA_202608211130.dat", Settled(agent), "11:30,21.2\n");

        await agent.Cycle.RunAsync();

        Assert.Equal(1, agent.Files.EnumerationsOf(Folder));
        Assert.Equal("11:30,21.2\n", agent.Server.Held(11, "GARISSA_202608211130.dat")!.Text);
    }

    [Fact]
    public async Task A_station_ADL_moves_back_onto_enumerate_walks_its_whole_folder_again()
    {
        await using var agent = new AgentHarness();

        var watermark = Watermark(agent, minutes: 30);

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.DirectFetchLink(11, Folder, watermark: watermark));

        agent.Files
            .Add(Folder, "GARISSA_202608211140.dat", Settled(agent), "11:40,21.4\n")
            .Add(Folder, "GARISSA_yesterday.dat", Settled(agent), "who knows\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Null(agent.Server.Held(11, "GARISSA_yesterday.dat"));

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat", watermark));

        await agent.Cycle.RunAsync();

        // Reconciled on the first cycle it enumerates, because as far as the
        // sweep log is concerned this station has never been swept at all --
        // which is exactly right for one whose folder nobody has listed.
        Assert.Equal(1, agent.Files.EnumerationsOf(Folder));
        Assert.Equal("who knows\n", agent.Server.Held(11, "GARISSA_yesterday.dat")!.Text);
    }

    [Fact]
    public async Task Reconciling_a_direct_fetch_station_still_lists_nothing()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.DirectFetchLink(11, Folder, watermark: Watermark(agent, minutes: 30)));

        agent.Files.Add(Folder, "GARISSA_202608211140.dat", Settled(agent), "11:40,21.4\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // A day on, past the reconciliation interval. What reconciling means
        // here is reach and not a second way of looking: the station is
        // reconciled -- the log says so -- and it still asks for its files by
        // name, one at a time, and still never lists the folder.
        agent.Time.Advance(TimeSpan.FromHours(25));

        await agent.Cycle.RunAsync();

        // Both cycles reconciled it: the first because no machine had ever
        // swept this station, the second because the day came round.
        Assert.Equal(2, agent.Store.SweepWrites);
        Assert.Equal(0, agent.Files.EnumerationsOf(Folder));
        Assert.Equal("11:40,21.4\n", agent.Server.Held(11, "GARISSA_202608211140.dat")!.Text);
    }

    /// <summary>The watermark as ADL would send it, a given way back.</summary>
    private static DateTimeOffset Watermark(AgentHarness agent, int minutes) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(minutes);

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
}
