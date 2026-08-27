using AdlAgent.Core.Diagnostics;
using AdlAgent.TestSupport;
using AdlAgent.Tray;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The passes window: what it asks for, and what it says when there is
/// nothing to show.
/// </summary>
/// <remarks>
/// Driven through the real transport, the same distance as the rest of the
/// local UI, because the arrangement under test is the one a substitute would
/// hide: the filters are the service's work, and the window is thin on
/// purpose.
/// </remarks>
public class PassesWindowTests
{
    private const string Garissa = "C:\\VendorData\\Garissa";
    private const string Kisumu = "C:\\VendorData\\Kisumu";

    [Fact]
    public async Task The_table_lists_every_unit_this_machine_has_passed_over()
    {
        await using var shown = await Collecting();

        var passes = shown.Window.Passes();

        await passes.RefreshAsync();

        // Two folders, so two units, so two rows.
        Assert.Equal(2, passes.Rows.Count);
        Assert.Contains(passes.Rows, row => row.Folder == Garissa);
        Assert.Contains(passes.Rows, row => row.Folder == Kisumu);

        // And the counts are the units' own, which is what the heading says.
        Assert.Equal("Unit totals", passes.CountsHeading);
        Assert.All(passes.Rows, row => Assert.Null(row.Station));
    }

    [Fact]
    public async Task Opening_it_from_a_station_row_arrives_filtered_to_that_station()
    {
        await using var shown = await Collecting();

        var passes = shown.Window.Passes(12);

        await passes.RefreshAsync();

        var row = Assert.Single(passes.Rows);

        Assert.Equal(Kisumu, row.Folder);

        // The counts are that station's, and the column heading names it --
        // so the change from unit totals is visible rather than silent.
        Assert.Equal("Station 12", row.Station);
        Assert.Equal("Station 12", passes.CountsHeading);
    }

    [Fact]
    public async Task Filtering_is_the_services_work_so_a_page_is_a_page_of_matches()
    {
        await using var shown = await Collecting();

        var passes = shown.Window.Passes();

        passes.ProblemsOnly = true;

        await passes.RefreshAsync();

        // Both stations collected cleanly, so there is nothing wrong to show
        // -- and the window says which kind of nothing it is.
        Assert.Empty(passes.Rows);
        Assert.True(passes.HasProblem);
        Assert.Contains("with problems", passes.Problem);
    }

    [Fact]
    public async Task A_machine_that_has_collected_nothing_is_told_so_rather_than_shown_a_blank()
    {
        await using var shown = await Collecting(collect: false);

        var passes = shown.Window.Passes();

        await passes.RefreshAsync();

        Assert.False(passes.HasRows);
        Assert.Contains("not recorded a collection pass", passes.Problem);
    }

    [Fact]
    public async Task A_table_that_holds_everything_says_so_rather_than_offering_more()
    {
        await using var shown = await Collecting();

        var passes = shown.Window.Passes();

        await passes.RefreshAsync();

        // The three answers that look alike: this is the one that means "that
        // is all of it", and it must not offer a button that would find
        // nothing.
        Assert.False(passes.CanLoadMore);
        Assert.Contains("all this machine has recorded", passes.Reach);
    }

    [Fact]
    public async Task Opening_a_row_fetches_the_file_detail_the_row_does_not_carry()
    {
        await using var shown = await Collecting();

        var passes = shown.Window.Passes(11);

        await passes.RefreshAsync();

        var row = Assert.Single(passes.Rows);

        passes.SelectedPass = row;

        await WaitFor(() => passes.HasDetail || passes.HasDetailProblem);

        Assert.True(passes.HasDetail, passes.DetailProblem);

        var detail = passes.Detail!;

        Assert.Contains(detail.Files, file => file.Name == "GARISSA_20260821.dat");
        Assert.Contains(Garissa, detail.Walked);

        // Copy puts the text form on the clipboard, so what a technician
        // pastes into an email is what the bundle says.
        Assert.Contains("GARISSA_20260821.dat", detail.Text);
    }

    [Fact]
    public async Task A_pass_written_over_since_the_row_was_drawn_is_a_sentence_and_not_a_fault()
    {
        await using var shown = await Collecting();

        var passes = shown.Window.Passes();

        await passes.RefreshAsync();

        // A row for a pass this machine never recorded, which is what a row
        // drawn before an eviction becomes. Ordinary on a machine working
        // through a backlog.
        passes.SelectedPass = new PassRowViewModel(new CyclePassRow
        {
            At = TestClock.Start.AddYears(-1),
            Unit = Garissa,
            Trigger = CycleTriggers.Scheduled,
            Seconds = 1,
            Completed = true,
            Scanned = 0,
            Held = 0,
            Offered = 0,
            Wanted = 0,
            Uploaded = 0,
            Failed = 0,
            Backlog = 0,
            Problem = false,
        });

        await WaitFor(() => passes.HasDetail || passes.HasDetailProblem);

        Assert.False(passes.HasDetail);
        Assert.Contains("no longer in the machine's log", passes.DetailProblem);
    }

    [Fact]
    public async Task The_station_filter_offers_every_station_and_all_of_them()
    {
        await using var shown = await Collecting();

        var passes = shown.Window.Passes();

        Assert.Equal(
            ["All stations", "Station 11", "Station 12"],
            passes.Stations.Select(choice => choice.Name));

        // Null is the whole machine, which is what the window opens on from
        // the Status tab.
        Assert.Null(passes.Stations[0].StationLinkId);
    }

    [Fact]
    public async Task Save_these_sends_what_the_technician_is_looking_at()
    {
        await using var shown = await Collecting();

        var passes = shown.Window.Passes(12);

        await passes.RefreshAsync();

        var path = Path.Combine(
            Directory.CreateTempSubdirectory("adl-agent-these").FullName, "diagnostics.txt");

        try
        {
            await passes.SaveAsync(path);

            Assert.Contains("Saved to", passes.Message);

            var bundle = await File.ReadAllTextAsync(path);

            // The hole this closes: a bundle that always carried the newest
            // two hundred passes could not hold the thing the window had just
            // been used to find.
            Assert.Contains("station link 12", bundle);
            Assert.Contains(Kisumu, bundle);
            Assert.DoesNotContain("GARISSA_20260821.dat", bundle);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private static async Task WaitFor(Func<bool> settled)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!settled() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    private static async Task<Shown> Collecting(bool collect = true)
    {
        var agent = new AgentHarness();

        ServedAgent? serving = null;

        try
        {
            serving = await ServedAgent.ServingAsync(agent);

            agent.Server.Config = SyncConfigs.Serving(
                SyncConfigs.Connection(
                    3,
                    "Vaisala AWS",
                    stationLinks:
                    [
                        SyncConfigs.Link(11, Garissa, "*.dat"),
                        SyncConfigs.Link(12, Kisumu, "*.dat"),
                    ]));

            agent.Files
                .Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "09:00,21.4\n")
                .Add(Kisumu, "KISUMU_20260821.dat", Settled(agent), "09:00,19.8\n");

            await agent.PairAsync();

            if (collect)
            {
                await agent.Cycle.RunAsync();
                await agent.CycleLog.FlushAsync();
            }
            else
            {
                await agent.Configuration.RefreshAsync();
            }

            var window = new ShellViewModel(serving.Link);

            await window.RefreshAsync();

            return new Shown(agent, serving, window);
        }
        catch
        {
            serving?.Dispose();

            await agent.DisposeAsync();

            throw;
        }
    }

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);

    private sealed class Shown(AgentHarness agent, ServedAgent serving, ShellViewModel window)
        : IAsyncDisposable
    {
        public ShellViewModel Window { get; } = window;

        public async ValueTask DisposeAsync()
        {
            serving.Dispose();

            await agent.DisposeAsync();
        }
    }
}
