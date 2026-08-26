using System.Text.Json;
using AdlAgent.Core.Api;
using AdlAgent.Core.Cycle;
using AdlAgent.Core.Serialization;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// A station whose vendor files by date, and the folders that means.
/// </summary>
/// <remarks>
/// The vendor does not write into the folder an administrator typed; it
/// writes into <c>2026\08\21</c> below it. ADL has let a station say so for
/// as long as the FTP plugin has existed, and until this landed the agent
/// walked the folder itself, found nothing there, and had nothing but a
/// sentence to offer.
/// <para>
/// Everything here is about the three ways that goes wrong quietly: the tree
/// carved in the wrong timezone (so the folder the agent looks in is the one
/// the vendor stopped writing to three hours ago), the whole tree walked
/// every cycle (so a year of hourly folders is 8,760 enumerations every ten
/// minutes), and a folder the vendor has not created yet reported as a fault
/// (so every station filed by day says something is wrong every midnight).
/// </para>
/// </remarks>
public class DatedFolderTests
{
    private const string Root = "C:\\VendorData\\Garissa";

    [Theory]
    // The clock is 2026-08-21T09:00Z, which is midday in Nairobi.
    [InlineData("year", "2026")]
    [InlineData("month", "2026\\08")]
    [InlineData("day", "2026\\08\\21")]
    [InlineData("hour", "2026\\08\\21\\12")]
    public async Task Files_are_collected_from_the_dated_folder_the_granularity_names(
        string granularity, string below)
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Dated(granularity));

        agent.Files.Add($"{Root}\\{below}", "GARISSA_20260821.dat", Settled(agent), "g\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal("g\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
    }

    [Theory]
    [InlineData("m", "08")]
    [InlineData("n", "8")]
    [InlineData("M", "Aug")]
    [InlineData("b", "aug")]
    [InlineData("F", "August")]
    [InlineData("f", "august")]
    public async Task The_month_folder_is_spelled_the_way_ADL_says_it_is(
        string format, string month)
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Dated("day", monthDirFormat: format));

        agent.Files.Add($"{Root}\\2026\\{month}\\21", "GARISSA_20260821.dat", Settled(agent), "g\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal("g\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
    }

    [Fact]
    public async Task An_absent_month_format_is_the_two_digit_one_ADL_defaults_to()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Dated("month", monthDirFormat: null));

        agent.Files.Add($"{Root}\\2026\\08", "GARISSA_20260821.dat", Settled(agent), "g\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal("g\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
    }

    [Fact]
    public async Task The_tree_is_carved_in_the_stations_timezone_and_not_this_machines()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Dated("day"));

        // Ten in the evening, UTC. In Nairobi it is one in the morning of the
        // 22nd, so that is the folder the vendor is writing into -- and a
        // machine that read the date off its own UTC clock would spend the
        // first three hours of every local day looking in the 21st.
        agent.Time.Advance(TimeSpan.FromHours(13));

        agent.Files.Add($"{Root}\\2026\\08\\22", "GARISSA_MIDNIGHT.dat", Settled(agent), "after midnight\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal("after midnight\n", agent.Server.Held(11, "GARISSA_MIDNIGHT.dat")!.Text);
        Assert.Equal(1, agent.Files.EnumerationsOf($"{Root}\\2026\\08\\22"));
    }

    [Fact]
    public async Task An_ordinary_cycle_walks_a_bounded_window_and_the_sweep_walks_the_rest()
    {
        await using var agent = new AgentHarness();

        // A station collecting since the start of the year, filed by day.
        // Walking that tree every cycle would be 233 enumerations every ten
        // minutes for a station whose files are all in today's folder.
        agent.Server.Config = SyncConfigs.With(
            Dated("day", startDate: DateTimeOffset.Parse("2026-01-01T00:00:00Z")));

        agent.Files.Add($"{Root}\\2026\\08\\21", "GARISSA_20260821.dat", Settled(agent), "today\n");

        await agent.PairAsync();

        // The first cycle of a fresh install is a sweep -- nobody has ever
        // reconciled this station -- so it is the one pass that sees the whole
        // tree, and getting past it is what leaves an ordinary cycle to watch.
        await agent.Cycle.RunAsync();

        Assert.Equal("today\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);

        // A file recovered off a dead logger and copied into the folder it
        // belongs to, five months back down the tree.
        agent.Files.Add($"{Root}\\2026\\03\\04", "GARISSA_20260304.dat", Settled(agent), "in March\n");

        var walkedBefore = WalkedFolders(agent);

        await agent.Cycle.RunAsync();

        // The default window is two days, so at day granularity that is
        // today, yesterday and the day before -- and March is not walked at
        // all rather than walked and filtered.
        Assert.Null(agent.Server.Held(11, "GARISSA_20260304.dat"));
        Assert.Equal(1, agent.Files.EnumerationsOf($"{Root}\\2026\\03\\04"));
        Assert.Equal(3, WalkedFolders(agent) - walkedBefore);

        // A day later the station stops trusting the cheap path, and the deep
        // pass is what reaches back to the collection start date.
        agent.Time.Advance(TimeSpan.FromHours(25));

        await agent.Cycle.RunAsync();

        Assert.Equal("in March\n", agent.Server.Held(11, "GARISSA_20260304.dat")!.Text);
    }

    [Fact]
    public async Task How_far_back_an_ordinary_cycle_walks_is_ADLs_to_say()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs
            .With(Dated("day", startDate: DateTimeOffset.Parse("2026-01-01T00:00:00Z")))
            // Sweeps off, so that what is collected here is the ordinary
            // cycle's reach and not the backstop's.
            .ReconcilingEvery(0)
            .WalkingDatedFoldersBack(24 * 10);

        agent.Files.Add($"{Root}\\2026\\08\\15", "GARISSA_20260815.dat", Settled(agent), "last week\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal("last week\n", agent.Server.Held(11, "GARISSA_20260815.dat")!.Text);
        Assert.Equal(11, WalkedFolders(agent));
    }

    [Fact]
    public async Task A_window_of_nothing_is_the_current_folder_alone()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs
            .With(Dated("day", startDate: DateTimeOffset.Parse("2026-01-01T00:00:00Z")))
            .ReconcilingEvery(0)
            .WalkingDatedFoldersBack(0);

        agent.Files
            .Add($"{Root}\\2026\\08\\21", "GARISSA_20260821.dat", Settled(agent), "today\n")
            .Add($"{Root}\\2026\\08\\20", "GARISSA_20260820.dat", Settled(agent), "yesterday\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal("today\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
        Assert.Null(agent.Server.Held(11, "GARISSA_20260820.dat"));
        Assert.Equal(1, WalkedFolders(agent));
    }

    [Fact]
    public async Task Two_stations_sharing_a_dated_tree_walk_it_once_between_them()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            Dated("day", id: 11, pattern: "GARISSA_*.dat"),
            // The same tree, spelled with a trailing separator. Still one
            // tree, and a technician who typed it that way is not wrong.
            Dated("day", id: 12, root: Root + "\\", pattern: "MOMBASA_*.dat"));

        agent.Files
            .Add($"{Root}\\2026\\08\\21", "GARISSA_20260821.dat", Settled(agent), "g\n")
            .Add($"{Root}\\2026\\08\\21", "MOMBASA_20260821.dat", Settled(agent), "m\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal(1, agent.Files.EnumerationsOf($"{Root}\\2026\\08\\21"));
        Assert.Equal("g\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
        Assert.Equal("m\n", agent.Server.Held(12, "MOMBASA_20260821.dat")!.Text);
    }

    [Fact]
    public async Task A_dated_folder_the_vendor_has_not_written_yet_is_a_quiet_non_event()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Dated("day"));

        // Yesterday's folder holds the station's files; today's does not
        // exist, because the vendor has not written the day's first file. On
        // a ten-minute cycle every station filed by day is in this state for
        // part of every day.
        agent.Files.Add($"{Root}\\2026\\08\\20", "GARISSA_20260820.dat", Settled(agent), "yesterday\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Null(link.Error);
        Assert.Equal(0, link.Failed);
        Assert.Equal("yesterday\n", agent.Server.Held(11, "GARISSA_20260820.dat")!.Text);
    }

    [Fact]
    public async Task A_station_that_finds_nothing_anywhere_in_its_tree_still_says_so()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Dated("day"));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        // One sentence for the station rather than one per empty folder, and
        // it names the folder an administrator typed rather than a directory
        // the agent worked out.
        Assert.Contains("dated folder", link.Error!);
        Assert.Contains(Root, link.Error!);
    }

    [Fact]
    public async Task A_station_filed_by_date_with_no_granularity_says_which_setting_is_missing()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Dated(granularity: null));

        agent.Files.Add($"{Root}\\2026\\08\\21", "GARISSA_20260821.dat", Settled(agent), "g\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Contains("granularity", link.Error!);
        Assert.Empty(agent.Server.ManifestPages);
    }

    [Fact]
    public async Task A_month_format_this_agent_cannot_write_is_said_out_loud()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Dated("day", monthDirFormat: "MMMM"));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Contains("month folder format", link.Error!);

        // And nothing was walked on a guess.
        Assert.DoesNotContain(agent.Files.Enumerations, folder => folder.Value > 0);
    }

    [Fact]
    public async Task A_month_format_is_only_judged_where_a_month_folder_is_written()
    {
        await using var agent = new AgentHarness();

        // Filed by year, so there is no month folder and the format is a
        // setting that does nothing. Refusing the station over it would stop
        // it collecting for a value it will never write.
        agent.Server.Config = SyncConfigs.With(Dated("year", monthDirFormat: "MMMM"));

        agent.Files.Add($"{Root}\\2026", "GARISSA_20260821.dat", Settled(agent), "g\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Null(link.Error);
        Assert.Equal("g\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);
    }

    [Fact]
    public async Task A_window_wider_than_a_cycle_walks_is_said_out_loud_rather_than_quietly_cut()
    {
        await using var agent = new AgentHarness();

        // Filed by hour and asked for three months of window, which is more
        // folders than a cycle walks. The cap is the right thing to do -- but
        // a cap that silently overrode the setting would be the setting not
        // working, and nobody would know which of the two numbers was in
        // force.
        agent.Server.Config = SyncConfigs
            .With(Dated("hour"))
            .ReconcilingEvery(0)
            .WalkingDatedFoldersBack(24 * 90);

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Contains(DatedFolders.MostPerCycle.ToString(), link.Error!);
        Assert.Equal(DatedFolders.MostPerCycle, WalkedFolders(agent));
    }

    [Fact]
    public async Task A_timezone_this_machine_does_not_know_is_said_rather_than_guessed_at()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(Dated("day", timezone: "Mars/Olympus_Mons"));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Contains("Mars/Olympus_Mons", link.Error!);
    }

    [Fact]
    public async Task A_station_moved_between_a_flat_folder_and_a_dated_tree_follows_ADL()
    {
        await using var agent = new AgentHarness();

        // Bound wrongly to begin with: the vendor files by date and the
        // station says it does not, which is the state every one of these
        // stations is in until somebody ticks the box.
        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Root, "GARISSA_*.dat"));

        agent.Files.Add($"{Root}\\2026\\08\\21", "GARISSA_20260821.dat", Settled(agent), "g\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Null(agent.Server.Held(11, "GARISSA_20260821.dat"));
        Assert.Equal(1, agent.Files.EnumerationsOf(Root));

        agent.Server.Config = SyncConfigs.With(Dated("day"));

        await agent.Cycle.RunAsync();

        Assert.Equal("g\n", agent.Server.Held(11, "GARISSA_20260821.dat")!.Text);

        // And back again, without a restart: the tree is not walked any more.
        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Root, "GARISSA_*.dat"));

        await agent.Cycle.RunAsync();

        Assert.Equal(1, agent.Files.EnumerationsOf($"{Root}\\2026\\08\\21"));
        Assert.Equal(2, agent.Files.EnumerationsOf(Root));
    }

    [Fact]
    public async Task A_file_below_the_watermark_is_left_alone_wherever_it_is_filed()
    {
        await using var agent = new AgentHarness();

        // ADL holds everything up to this morning, so yesterday's folder is
        // still walked -- the window is about directories, not files -- and
        // what is in it is still filtered on the watermark.
        agent.Server.Config = SyncConfigs
            .With(Dated("day", watermark: TestClock.Start - TimeSpan.FromHours(2)))
            .ReconcilingEvery(0);

        agent.Files
            .Add($"{Root}\\2026\\08\\21", "GARISSA_NEW.dat", Settled(agent), "this morning\n")
            .Add($"{Root}\\2026\\08\\20", "GARISSA_OLD.dat",
                TestClock.Start - TimeSpan.FromHours(20), "yesterday\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal("this morning\n", agent.Server.Held(11, "GARISSA_NEW.dat")!.Text);
        Assert.Null(agent.Server.Held(11, "GARISSA_OLD.dat"));
        Assert.Equal(1, agent.Files.EnumerationsOf($"{Root}\\2026\\08\\20"));
    }

    [Fact]
    public async Task A_station_that_builds_its_filenames_and_files_by_date_is_told_the_two_do_not_go_together()
    {
        await using var agent = new AgentHarness();

        var fetching = SyncConfigs.DirectFetchLink(11, Root);

        agent.Server.Config = SyncConfigs.With(
            fetching with { Config = fetching.Config with { DirStructuredByDate = true } });

        // The name it builds for the current interval, in the folder ADL
        // named -- which is where it looks, tree or no tree.
        agent.Files.Add(Root, "GARISSA_202608211200.dat", Settled(agent), "g\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Contains("dated sub-folders", link.Error!);

        // And it still collects, from the folder it was pointed at: the
        // sentence is a warning about a setting, not a reason to stop.
        Assert.Equal("g\n", agent.Server.Held(11, "GARISSA_202608211200.dat")!.Text);
    }

    [Fact]
    public void The_window_ADL_sends_is_the_window_this_agent_reads()
    {
        // A literal body rather than a round trip through this agent's own
        // serializer, which would prove only that it agrees with itself. What
        // is pinned here is the spelling the plugin emits -- ADL and the agent
        // are separate repositories on separate release trains, and a field
        // renamed on one side would otherwise show up as every dated station
        // in the fleet quietly walking the default window instead.
        const string body = """
            {
              "id": 3,
              "name": "Songea server",
              "check_interval_minutes": 5,
              "heartbeat_interval_minutes": 5,
              "dated_folder_window_hours": 240
            }
            """;

        var device = JsonSerializer.Deserialize<DeviceConfig>(body, AgentJson.Options)!;

        Assert.Equal(240, device.DatedFolderWindowHours);
        Assert.Equal(TimeSpan.FromHours(240), DatedFolders.RecentWindow(device.DatedFolderWindowHours));
    }

    [Fact]
    public void An_ADL_that_predates_the_window_gets_the_default_and_not_a_zero()
    {
        // The reason the field is nullable. Absent and zero are opposite
        // instructions -- "you decide" and "today's folder only" -- and an
        // int would have made them the same value.
        var older = JsonSerializer.Deserialize<DeviceConfig>(
            """{"id": 3, "check_interval_minutes": 5}""", AgentJson.Options)!;

        Assert.Null(older.DatedFolderWindowHours);
        Assert.Equal(DatedFolders.DefaultRecentWindow, DatedFolders.RecentWindow(older.DatedFolderWindowHours));

        var asked = JsonSerializer.Deserialize<DeviceConfig>(
            """{"id": 3, "dated_folder_window_hours": 0}""", AgentJson.Options)!;

        Assert.Equal(TimeSpan.Zero, DatedFolders.RecentWindow(asked.DatedFolderWindowHours));
    }

    /// <summary>
    /// How many of this station's dated folders the cycle looked in.
    /// </summary>
    /// <remarks>
    /// The number the bound exists to hold down. A station filed by hour with
    /// a start date a year back is 8,760 directories, and what is asserted is
    /// that an ordinary cycle does not go near that.
    /// </remarks>
    private static int WalkedFolders(AgentHarness agent) =>
        agent.Files.Enumerations
            .Where(folder => folder.Key.StartsWith(Root + "\\", StringComparison.Ordinal))
            .Sum(folder => folder.Value);

    private static Api.StationLinkConfig Dated(
        string? granularity,
        long id = 11,
        string root = Root,
        string pattern = "GARISSA_*.dat",
        string? monthDirFormat = "m",
        string timezone = "Africa/Nairobi",
        DateTimeOffset? watermark = null,
        DateTimeOffset? startDate = null) =>
        SyncConfigs.Link(
            id, root, pattern,
            watermark: watermark,
            dirStructuredByDate: true,
            dateGranularity: granularity,
            monthDirFormat: monthDirFormat,
            timezone: timezone,
            startDate: startDate);

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
}
