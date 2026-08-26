using System.Diagnostics;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// What a cycle costs a folder, and what it tells HQ about one.
/// </summary>
/// <remarks>
/// The folders this product exists for are the ones nobody wants to think
/// about: one dump directory holding a minute-by-minute file for every
/// station in the country, going back years. Everything here is about that
/// folder -- walked once however many stations share it, read only where the
/// window says something changed, and reported on in sentences an operator on
/// another continent can act on.
/// </remarks>
public class FolderScanTests
{
    private const string Shared = "C:\\VendorData\\All";

    [Fact]
    public async Task One_folder_serving_three_stations_is_walked_once()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, Shared, "GARISSA_*.dat"),
            SyncConfigs.Link(12, Shared, "MOMBASA_*.dat"),
            // The same folder, spelled with a trailing separator. Still one
            // folder, and a technician who typed it that way is not wrong.
            SyncConfigs.Link(13, Shared + "\\", "KISUMU_*.dat"));

        agent.Files
            .Add(Shared, "GARISSA_20260821.dat", Settled(agent), "g\n")
            .Add(Shared, "MOMBASA_20260821.dat", Settled(agent), "m\n")
            .Add(Shared, "KISUMU_20260821.dat", Settled(agent), "k\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal(1, agent.Files.EnumerationsOf(Shared));

        // And each file reached its own station's ledger row.
        Assert.Equal("g\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
        Assert.Equal("m\n", agent.Server.Held(12, "MOMBASA_20260821.dat")!.Text);
        Assert.Equal("k\n", agent.Server.Held(13, "KISUMU_20260821.dat")!.Text);
    }

    [Fact]
    public async Task Two_folders_differing_only_in_case_are_two_folders()
    {
        await using var agent = new AgentHarness();

        // Whether these are one directory or two is the filesystem's answer,
        // not the agent's, so the agent does not fold them together. Being
        // wrong this way costs a Windows server one extra walk; folding them
        // would have a Linux server scan the wrong folder for one of the two
        // stations and never say a word about it.
        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, "/vendor/garissa", "*.dat"),
            SyncConfigs.Link(12, "/vendor/GARISSA", "*.dat"));

        agent.Files
            .Add("/vendor/garissa", "one.dat", Settled(agent), "lower\n")
            .Add("/vendor/GARISSA", "two.dat", Settled(agent), "upper\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal("lower\n", agent.Server.Held(11, "one.dat")!.Text);
        Assert.Equal("upper\n", agent.Server.Held(12, "two.dat")!.Text);
    }

    [Fact]
    public async Task A_file_no_pattern_claims_is_left_where_it_is()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Shared, "GARISSA_??????.dat"));

        agent.Files
            .Add(Shared, "GARISSA_260821.dat", Settled(agent), "mine\n")
            .Add(Shared, "GARISSA_20260821.dat", Settled(agent), "too many digits\n")
            .Add(Shared, "readme.txt", Settled(agent), "not data at all\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var offered = Assert.Single(agent.Server.ManifestPages);

        Assert.Equal("GARISSA_260821.dat", Assert.Single(offered).Name);
    }

    [Fact]
    public async Task An_untouched_file_is_read_once_however_many_cycles_run()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Shared, "*.dat"));
        agent.Files.Add(Shared, "GARISSA_1.dat", Settled(agent), "row 1\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal("row 1\n", agent.Server.Held(11, "GARISSA_1.dat")!.Text);

        var offeredAs = agent.Server.ManifestPages[0][0].Hash;

        // The bytes are taken away and the folder goes on describing the file
        // exactly as before. Nothing on a real disk behaves like this -- it is
        // how a test can see that nothing opened the file.
        agent.Files.Vanish(Shared, "GARISSA_1.dat");

        await agent.Cycle.RunAsync();
        await agent.Cycle.RunAsync();

        // Still offered, still under the same hash, still no failure: the walk
        // handed over the size and the time, and that answered the question.
        Assert.Equal(offeredAs, agent.Server.ManifestPages[2].Single().Hash);
        Assert.Equal(0, agent.Cycles.LastCompletedCycle!.Links.Single().Failed);
    }

    [Fact]
    public async Task A_file_that_changed_is_read_again()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Shared, "*.dat"));
        agent.Files.Add(Shared, "GARISSA_1.dat", Settled(agent), "row 1\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // The same trick, with the file's size and time moved on as an append
        // would move them. Now the memo cache must not answer -- and the read
        // it is forced into is the read that finds the bytes gone.
        agent.Files.Vanish(Shared, "GARISSA_1.dat");
        agent.Time.Advance(TimeSpan.FromMinutes(30));
        agent.Files.State(Shared, "GARISSA_1.dat", Settled(agent), length: 99);

        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Equal(1, link.Failed);
        Assert.Contains("could not be read", link.Error!);
    }

    [Fact]
    public async Task A_folder_of_a_hundred_thousand_files_costs_a_walk_and_five_reads()
    {
        await using var agent = new AgentHarness();

        var watermark = agent.Time.GetUtcNow() - TimeSpan.FromDays(1);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Shared, "*.dat", watermark));

        // Years of history, stated but never written: if the cycle opened one
        // of these it would not find it, which is the assertion this fixture
        // is making. The walk sees all hundred thousand; nothing else does.
        var ancient = watermark - TimeSpan.FromDays(365);

        for (var index = 0; index < 100_000; index++)
        {
            agent.Files.State(Shared, $"OLD_{index:000000}.dat", ancient.AddMinutes(index), length: 512);
        }

        for (var index = 0; index < 5; index++)
        {
            agent.Files.Add(Shared, $"NEW_{index}.dat", Settled(agent), $"today {index}\n");
        }

        await agent.PairAsync();

        var clock = Stopwatch.StartNew();

        await agent.Cycle.RunAsync();

        clock.Stop();

        Assert.Equal(1, agent.Files.EnumerationsOf(Shared));

        // The five above the watermark, and only those. The hundred thousand
        // below it were never written to disk, so a cycle that opened one
        // would have failed here rather than merely been slow.
        Assert.Equal(5, agent.Server.Ledger.Count);
        Assert.Equal(0, agent.Cycles.LastCompletedCycle!.Links.Single().Failed);

        // Wildly generous, and still nowhere near a folder that has to be
        // re-hashed: the point is that the cost of a settled folder is the
        // walk, and the walk is metadata the platform hands over for free.
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(30),
            $"A cycle over a hundred thousand files took {clock.Elapsed}.");
    }

    [Fact]
    public async Task A_folder_this_machine_cannot_see_is_reported_against_the_station()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, "C:\\VendorData\\Typo", "*.dat"));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Equal(0, link.Scanned);
        Assert.Contains("C:\\VendorData\\Typo", link.Error!);
    }

    [Fact]
    public async Task A_pattern_that_matches_none_of_the_folder_says_how_many_it_looked_at()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Shared, "GARISSA_*.dat"));

        agent.Files
            .Add(Shared, "garissa20260821.DAT", Settled(agent), "vendor renamed things\n")
            .Add(Shared, "readme.txt", Settled(agent), "\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        // The sentence a technician needs: the folder is right and the
        // pattern is not, which is a different phone call from "no folder".
        Assert.Contains("None of the 2 files", link.Error!);
        Assert.Contains("GARISSA_*.dat", link.Error!);
    }

    [Fact]
    public async Task A_station_switched_off_in_ADL_is_not_scanned_at_all()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, Shared, "GARISSA_*.dat"),
            SyncConfigs.Link(12, Shared, "MOMBASA_*.dat", enabled: false));

        agent.Files
            .Add(Shared, "GARISSA_20260821.dat", Settled(agent), "g\n")
            .Add(Shared, "MOMBASA_20260821.dat", Settled(agent), "m\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Null(agent.Server.Held(12, "MOMBASA_20260821.dat"));

        // Not reported against either: an administrator switching a station
        // off is a decision, not a fault of the machine's.
        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Equal(11, link.StationLinkId);
    }

    [Fact]
    public async Task A_connection_switched_off_in_ADL_takes_its_stations_with_it()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            connectionEnabled: false, SyncConfigs.Link(11, Shared, "*.dat"));

        agent.Files.Add(Shared, "GARISSA_20260821.dat", Settled(agent), "g\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal(0, agent.Files.EnumerationsOf(Shared));
        Assert.Empty(agent.Cycles.LastCompletedCycle!.Links);
    }

    [Fact]
    public async Task A_station_the_agent_cannot_scan_says_so_rather_than_doing_nothing()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            // A strategy from an ADL newer than this agent. Walking the
            // folder anyway would be the worst of the three things it could
            // do: a strategy nobody has heard of is usually one chosen
            // because the folder is enormous.
            SyncConfigs.Link(11, Shared, "*.dat", listingStrategy: "usn_journal"),
            SyncConfigs.Link(12, Shared, pattern: ""));

        agent.Files.Add(Shared, "GARISSA_20260821.dat", Settled(agent), "g\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var links = agent.Cycles.LastCompletedCycle!.Links;

        Assert.Contains("usn_journal", links.Single(link => link.StationLinkId == 11).Error!);

        // A folder is nearly always shared, so guessing "every file" for a
        // station with no pattern would file one station's data under another.
        Assert.Contains("No file pattern", links.Single(link => link.StationLinkId == 12).Error!);
        Assert.Equal(0, agent.Files.EnumerationsOf(Shared));
        Assert.Empty(agent.Server.ManifestPages);
    }

    [Theory]
    // What a real instance actually sends: the plugin's listing strategy is
    // a Django TextChoices whose stored form is lower case. An agent looking
    // for "ENUMERATE" scans nothing, anywhere, on every machine in the fleet
    // -- and the folders would look fine to anyone who went and checked.
    [InlineData("enumerate")]
    [InlineData("ENUMERATE")]
    // Absent, because an older instance did not send the field at all.
    [InlineData("")]
    public async Task A_station_ADL_says_to_enumerate_is_enumerated(string strategy)
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, Shared, "*.dat", listingStrategy: strategy));

        agent.Files.Add(Shared, "GARISSA_20260821.dat", Settled(agent), "g\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal("g\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
    }

    [Fact]
    public async Task The_cycle_says_what_it_did_for_each_station()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, Shared, "GARISSA_*.dat"),
            SyncConfigs.Link(12, Shared, "MOMBASA_*.dat"));

        agent.Files
            .Add(Shared, "GARISSA_1.dat", Settled(agent), "g1\n")
            .Add(Shared, "GARISSA_2.dat", Settled(agent), "g2\n")
            .Add(Shared, "MOMBASA_1.dat", Settled(agent), "m1\n");

        // ADL already holds one of Garissa's two.
        agent.Server.Stage(11, "GARISSA_1.dat", "g1\n"u8.ToArray());

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var cycle = agent.Cycles.LastCompletedCycle!;

        Assert.Equal(agent.Time.GetUtcNow(), cycle.CompletedAt);

        var garissa = cycle.Links.Single(link => link.StationLinkId == 11);

        Assert.Equal(2, garissa.Scanned);
        Assert.Equal(2, garissa.Offered);
        Assert.Equal(1, garissa.Uploaded);
        Assert.Equal(0, garissa.Failed);
        Assert.Null(garissa.Error);

        var mombasa = cycle.Links.Single(link => link.StationLinkId == 12);

        Assert.Equal(1, mombasa.Scanned);
        Assert.Equal(1, mombasa.Uploaded);

        Assert.Equal(0, agent.Cycles.BacklogCount);
    }

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
}
