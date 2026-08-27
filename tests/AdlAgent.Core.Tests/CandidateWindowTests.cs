using AdlAgent.Core.Api;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Stories 15 and 16: recovered data reaches ADL weeks late, and a file that
/// is still being written never does.
/// </summary>
/// <remarks>
/// Every case here is arranged at the two platform seams rather than on the
/// disk, because every one of them is a Windows fact a Linux CI runner cannot
/// produce: a file that arrived today carrying last week's last-write time, a
/// vendor process holding its output open.
/// </remarks>
public class CandidateWindowTests
{
    private const string Folder = "C:\\VendorData\\Garissa";

    private static readonly DateTimeOffset Watermark = DateTimeOffset.Parse("2026-08-15T00:00:00Z");

    [Fact]
    public async Task A_file_older_than_the_watermark_is_not_offered()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = Config(Watermark);

        agent.Files
            .Add(Folder, "recent.dat", Watermark.AddDays(3), "in\n")
            .Add(Folder, "ancient.dat", Watermark.AddDays(-30), "out\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var offered = Assert.Single(agent.Server.ManifestPages);

        Assert.Equal("recent.dat", Assert.Single(offered).Name);
    }

    [Fact]
    public async Task A_file_backfilled_weeks_late_still_reaches_ADL()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = Config(Watermark);

        // Story 15. The file was written by the logger on the first of the
        // month and copied into the folder this morning; Windows gives the
        // copy a fresh creation time, and the seam windows on the later of
        // the two. Without that, this file falls behind the watermark and
        // nobody ever notices it did.
        var written = DateTimeOffset.Parse("2026-08-01T06:00:00Z");
        var arrived = agent.Time.GetUtcNow() - TimeSpan.FromMinutes(10);

        agent.Files.Add(
            Folder,
            "GARISSA_20260801.dat",
            PlatformWindowing.WindowsLike(written, arrived),
            "06:00,19.2\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal("06:00,19.2\n", agent.Server.Held(11, "GARISSA_20260801.dat")!.Text);
    }

    [Fact]
    public async Task A_file_still_being_written_is_left_for_the_next_cycle()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = Config(Watermark);

        // Inside the sixty-second stability window: the logger wrote to it
        // twenty seconds ago and may not have finished the line.
        agent.Files.Add(
            Folder, "GARISSA_20260821.dat", agent.Time.GetUtcNow() - TimeSpan.FromSeconds(20), "09:0");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Empty(agent.Server.ManifestPages);

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        // Seen, and deliberately not counted as a failure: in a live folder
        // the newest file is in this state on every single cycle.
        Assert.Equal(1, link.Scanned);
        Assert.Equal(0, link.Offered);
        Assert.Equal(0, link.Failed);

        // It is backlog, though -- ADL does not have it.
        Assert.Equal(1, agent.Cycles.BacklogCount);

        // The vendor finishes the line, and the window lets go.
        agent.Files.Append(
            Folder, "GARISSA_20260821.dat", "0,21.4\n", agent.Time.GetUtcNow());

        agent.Time.Advance(TimeSpan.FromMinutes(5));

        await agent.Cycle.RunAsync();

        Assert.Equal("09:00,21.4\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
    }

    [Fact]
    public async Task A_file_a_vendor_process_is_holding_open_is_left_alone()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = Config(Watermark);

        // Settled by the clock -- opened an hour ago and still being filled,
        // which the stability window alone cannot see.
        agent.Files.Add(
            Folder, "GARISSA_20260821.dat", agent.Time.GetUtcNow() - TimeSpan.FromHours(1), "09:00,21.4\n");

        agent.Readiness.LockedPaths.Add(agent.Files.PathOf(Folder, "GARISSA_20260821.dat"));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Empty(agent.Server.ManifestPages);

        agent.Readiness.LockedPaths.Clear();

        await agent.Cycle.RunAsync();

        Assert.Equal("09:00,21.4\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
    }

    [Fact]
    public async Task A_file_bigger_than_ADL_accepts_is_reported_rather_than_offered()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = Config(Watermark) with
        {
            Limits = new AgentLimits { ManifestEntries = 500, FileBytes = 64 },
        };

        agent.Files.Add(
            Folder, "GARISSA_20260821.dat", agent.Time.GetUtcNow() - TimeSpan.FromHours(1), length: 4096);

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // Offering it would spend a manifest slot and an upload, every cycle
        // forever, on a file ADL is required to refuse.
        Assert.Empty(agent.Server.ManifestPages);

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Equal(1, link.Failed);

        // The file is named in front of the reason, as every failure sentence
        // now is, so that the cycle log can fold five hundred identical ones
        // into a line and a count without the filename making each distinct.
        Assert.StartsWith("GARISSA_20260821.dat: ", link.Error!);
        Assert.Contains("larger than the 64 bytes ADL accepts", link.Error!);
    }

    [Fact]
    public async Task A_station_with_no_watermark_offers_everything_it_can_see()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = Config(watermark: null);

        agent.Files.Add(
            Folder, "GARISSA_20200101.dat", DateTimeOffset.Parse("2020-01-01T00:00:00Z"), "old\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal("old\n", agent.Server.Held(11, "GARISSA_20200101.dat")!.Text);
    }

    private static SyncResponse Config(DateTimeOffset? watermark) =>
        SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat", watermark));
}
