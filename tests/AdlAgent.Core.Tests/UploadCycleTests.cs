using AdlAgent.Core.Api;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Stories 13 and 14: propose a manifest and be told what to send, and never
/// silently lose the observations a logger appended after ADL had the file.
/// </summary>
public class UploadCycleTests
{
    private const string Folder = "C:\\VendorData\\Garissa";

    [Fact]
    public async Task A_cycle_offers_what_the_folder_holds_and_uploads_what_ADL_asks_for()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        agent.Files
            .Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n")
            .Add(Folder, "GARISSA_20260820.dat", Settled(agent), "09:00,20.9\n")
            // Another vendor's file in the same folder, which this station's
            // pattern says nothing about.
            .Add(Folder, "MOMBASA_20260821.dat", Settled(agent), "09:00,29.1\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var offered = Assert.Single(agent.Server.ManifestPages);

        Assert.Equal(
            ["GARISSA_20260820.dat", "GARISSA_20260821.dat"],
            offered.Select(entry => entry.Name).Order());

        Assert.Equal("09:00,21.4\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
        Assert.Equal("09:00,20.9\n", agent.Server.Held(11, "GARISSA_20260820.dat")!.Text);
        Assert.Null(agent.Server.Held(11, "MOMBASA_20260821.dat"));
    }

    [Fact]
    public async Task A_second_cycle_offers_the_same_files_and_ADL_asks_for_none()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));
        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();
        await agent.Cycle.RunAsync();

        // The agent is stateless: it offers the file again, every cycle, and
        // it is ADL's ledger that says "I have that one".
        Assert.Equal(2, agent.Server.ManifestPages.Count);
        Assert.All(agent.Server.ManifestPages, page => Assert.Single(page));

        Assert.Single(agent.Server.RequestsFor("files/"));
    }

    [Fact]
    public async Task A_file_that_grew_is_offered_again_and_replaces_what_ADL_held()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));
        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // The logger appends the next hour's observation.
        agent.Time.Advance(TimeSpan.FromHours(1));
        agent.Files.Append(Folder, "GARISSA_20260821.dat", "10:00,22.7\n", Settled(agent));

        await agent.Cycle.RunAsync();

        Assert.Equal(2, agent.Server.RequestsFor("files/").Count);
        Assert.Equal("09:00,21.4\n10:00,22.7\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
    }

    [Fact]
    public async Task A_file_ADL_refuses_is_offered_again_next_cycle()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));
        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

        // What a file being appended to mid-upload looks like from ADL: the
        // bytes no longer hash to what the manifest promised.
        agent.Server.RefusedUploads.Add("GARISSA_20260821.dat");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Null(agent.Server.Held(11, "GARISSA_20260821.dat"));

        var refused = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Equal(1, refused.Failed);
        Assert.Equal(0, refused.Uploaded);
        Assert.Contains("does not hash", refused.Error!);

        // The agent kept no note of the refusal. The folder is its memory,
        // and the file is still in it.
        agent.Server.RefusedUploads.Clear();

        await agent.Cycle.RunAsync();

        Assert.Equal("09:00,21.4\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
    }

    [Fact]
    public async Task A_backlog_is_offered_newest_first()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));

        // Six weeks of daily files, added oldest first so that arrival order
        // cannot be what produces the answer.
        var oldest = agent.Time.GetUtcNow() - TimeSpan.FromDays(42);

        for (var day = 0; day < 42; day++)
        {
            agent.Files.Add(
                Folder, $"GARISSA_{day:000}.dat", oldest.AddDays(day), $"day {day}\n");
        }

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var offered = agent.Server.ManifestPages.SelectMany(page => page).ToList();

        // Today's observations are in the first page, on the wire before the
        // history behind them has even been offered (story 18).
        Assert.Equal("GARISSA_041.dat", offered[0].Name);
        Assert.Equal(
            offered.Select(entry => entry.Mtime).OrderDescending(),
            offered.Select(entry => entry.Mtime));
    }

    [Fact]
    public async Task A_fresh_install_sends_todays_files_before_it_has_looked_at_last_years()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat")) with
        {
            Limits = new AgentLimits { ManifestEntries = 10, FileBytes = 50 * 1024 * 1024 },
        };

        // A year of daily files, all inside the window, as a machine paired
        // for the first time would find them.
        var oldest = agent.Time.GetUtcNow() - TimeSpan.FromDays(365);

        for (var day = 0; day < 365; day++)
        {
            agent.Files.Add(Folder, $"GARISSA_{day:000}.dat", oldest.AddDays(day), $"day {day}\n");
        }

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // Story 18, as it looks on the wire: the first page is the newest ten
        // files, and they are uploaded before the second page is even
        // offered. A cycle that manifested the whole year before sending
        // anything would show thirty-seven manifests and then three hundred
        // and sixty-five uploads.
        //
        // What this cannot see is that those ten files are also the only ten
        // that have been *read* at that point. That the scan hashes lazily is
        // a latency property with no signature at any seam -- the same calls
        // go out in the same order either way -- so no test asserts it; see
        // the remarks on FolderScanner.Hashing for why it matters anyway.
        var calls = agent.Server.Requests
            .Select(request => request.Path)
            .SkipWhile(path => path != "manifest/")
            .Take(12)
            .ToList();

        Assert.Equal("manifest/", calls[0]);
        Assert.Equal(Enumerable.Repeat("files/", 10), calls.Skip(1).Take(10));
        Assert.Equal("manifest/", calls[11]);

        Assert.Equal(
            ["GARISSA_364.dat", "GARISSA_363.dat", "GARISSA_362.dat"],
            agent.Server.ManifestPages[0].Take(3).Select(entry => entry.Name));

        Assert.Equal(365, agent.Server.Ledger.Count);
    }

    [Fact]
    public async Task A_manifest_too_large_for_one_call_is_sent_in_pages()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat")) with
        {
            Limits = new AgentLimits { ManifestEntries = 3, FileBytes = 50 * 1024 * 1024 },
        };

        for (var index = 0; index < 7; index++)
        {
            agent.Files.Add(
                Folder,
                $"GARISSA_{index:000}.dat",
                Settled(agent) - TimeSpan.FromMinutes(index),
                $"row {index}\n");
        }

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // The fake ADL refuses an over-long page exactly as a real one does,
        // so a cycle that ignored the limit would not merely be impolite.
        Assert.Equal([3, 3, 1], agent.Server.ManifestPages.Select(page => page.Count));
        Assert.Equal(7, agent.Server.Ledger.Count);
    }

    [Fact]
    public async Task A_revoked_token_stops_the_cycle_before_anything_is_offered()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));
        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

        await agent.PairAsync();

        agent.Server.TokenRevoked = true;

        await agent.Cycle.RunAsync();

        Assert.Equal(Core.Pairing.PairingState.RePairNeeded, agent.Session.State);
        Assert.Empty(agent.Server.ManifestPages);
        Assert.Empty(agent.Server.RequestsFor("files/"));

        // And it stays stopped: the next cycle does not even ask.
        var asked = agent.Server.Requests.Count;

        await agent.Cycle.RunAsync();

        Assert.Equal(asked, agent.Server.Requests.Count);
    }

    [Fact]
    public async Task A_cycle_with_nothing_to_offer_does_not_call_the_manifest()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // A round trip on a link that is metered, to say nothing at all.
        Assert.Empty(agent.Server.ManifestPages);
        Assert.NotNull(agent.Cycles.LastCompletedCycle);
    }

    [Fact]
    public async Task A_station_ADL_has_moved_away_is_named_rather_than_ignored()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));
        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n");

        // The machine's configuration says this station is its own; ADL has
        // since decided otherwise.
        agent.Server.StationLinksUnknownToAdl.Add(11);

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Equal(1, link.Offered);
        Assert.Equal(0, link.Uploaded);
        Assert.Contains("does not know this station", link.Error!);
    }

    /// <summary>A file the stability window has already let go of.</summary>
    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
}
