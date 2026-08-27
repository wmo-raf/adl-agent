using AdlAgent.Core.Diagnostics;
using AdlAgent.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Reading the record back as a table: the rows, the filters, and the two
/// answers that look identical and mean opposite things.
/// </summary>
/// <remarks>
/// The filters are the service's work rather than the window's, which is
/// what these pin. A window that narrowed the page it had been sent could
/// only ever narrow the newest few hundred passes -- and on a healthy
/// machine those are exactly the ones with nothing in them.
/// </remarks>
public class PassIndexTests : IDisposable
{
    private readonly string _folder =
        Directory.CreateTempSubdirectory("adl-agent-index").FullName;

    private readonly FakeTimeProvider _time = new(TestClock.Start);

    private CycleLogReader Reader => new(_folder);

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_row_carries_what_a_table_shows_and_not_the_file_detail()
    {
        Write(Pass(minutes: 0, uploaded: 3, files: 40));

        var row = Assert.Single(Reader.Index(new CyclePassQuery()).Rows);

        Assert.Equal("C:\\Vendor\\dump", row.Unit);
        Assert.Equal(CycleTriggers.Scheduled, row.Trigger);
        Assert.Equal(3, row.Uploaded);
        Assert.False(row.Problem);
    }

    [Fact]
    public void A_page_of_rows_is_a_fraction_of_a_page_of_records()
    {
        // The whole reason the index exists. Twenty full records do not fit in
        // one control message; hundreds of rows do.
        for (var minute = 0; minute < 20; minute++)
        {
            Write(Pass(minute, uploaded: 1, files: 60));
        }

        var rows = System.Text.Json.JsonSerializer.Serialize(
            Reader.Index(new CyclePassQuery()).Rows, Core.Serialization.AgentJson.Options);
        var records = System.Text.Json.JsonSerializer.Serialize(
            Reader.Recent(new CyclePassQuery()), Core.Serialization.AgentJson.Options);

        Assert.True(
            rows.Length * 8 < records.Length,
            $"Rows were {rows.Length} bytes against {records.Length} for the records.");
    }

    [Fact]
    public void Rows_come_back_newest_first()
    {
        Write(Pass(minutes: 0));
        Write(Pass(minutes: 10));
        Write(Pass(minutes: 20));

        var rows = Reader.Index(new CyclePassQuery()).Rows;

        Assert.Equal(
            [TestClock.Start.AddMinutes(20), TestClock.Start.AddMinutes(10), TestClock.Start],
            rows.Select(row => row.At));
    }

    // ---------- filters, applied by the reader ----------

    [Fact]
    public void Problems_only_is_one_switch_over_three_unrelated_faults()
    {
        Write(Pass(minutes: 0));
        Write(Pass(minutes: 10, failed: 2));
        Write(Pass(minutes: 20, completed: false));
        Write(Pass(minutes: 30, error: "No local folder is set for this station."));

        var found = Reader.Index(new CyclePassQuery(ProblemsOnly: true)).Rows;

        // A technician hunting trouble wants all three and should not first
        // have to know which kind they are looking for.
        Assert.Equal(3, found.Count);
        Assert.All(found, row => Assert.True(row.Problem));
    }

    [Fact]
    public void A_station_filter_is_the_reader_s_work_and_not_a_windows()
    {
        // The page is a page of matches, which is what makes "load more" walk
        // back through rows instead of blank screens.
        for (var minute = 0; minute < 30; minute++)
        {
            Write(Pass(minute, stationLinkId: minute % 2 == 0 ? 11 : 12));
        }

        var found = Reader.Index(new CyclePassQuery(StationLinkId: 12, Most: 15)).Rows;

        Assert.Equal(15, found.Count);
        Assert.All(found, row => Assert.Equal("Station 12", row.Station));
    }

    [Fact]
    public void Filtered_to_a_station_the_counts_are_that_stations_own()
    {
        // The misreading this exists to stop: forty stations in one dump
        // directory, one of them failing, and every other row reading as
        // though it had failed too.
        Write(new CycleRecord
        {
            At = TestClock.Start,
            Seconds = 41,
            Unit = "C:\\Vaisala\\dump",
            Trigger = CycleTriggers.Scheduled,
            Completed = true,
            Folders = [new CycleFolderRecord("C:\\Vaisala\\dump", 3840)],
            Stations =
            [
                Station(41, "Bobo-Dioulasso", failed: 12),
                Station(42, "Banfora", uploaded: 3),
            ],
            Files = [],
        });

        var banfora = Assert.Single(Reader.Index(new CyclePassQuery(StationLinkId: 42)).Rows);

        Assert.Equal("Banfora", banfora.Station);
        Assert.Equal(0, banfora.Failed);
        Assert.Equal(3, banfora.Uploaded);

        // Unfiltered, the same pass is the unit's totals, and the column
        // header says so by carrying no station name.
        var unit = Assert.Single(Reader.Index(new CyclePassQuery()).Rows);

        Assert.Null(unit.Station);
        Assert.Equal(12, unit.Failed);
    }

    [Fact]
    public void A_trigger_filter_answers_whether_the_nightly_sweep_ran()
    {
        Write(Pass(minutes: 0));
        Write(Pass(minutes: 10, trigger: CycleTriggers.Reconciliation));
        Write(Pass(minutes: 20));

        var swept = Assert.Single(
            Reader.Index(new CyclePassQuery(Trigger: CycleTriggers.Reconciliation)).Rows);

        Assert.Equal(TestClock.Start.AddMinutes(10), swept.At);
    }

    // ---------- paging, and saying how far it got ----------

    [Fact]
    public void A_full_page_says_there_is_more_and_where_to_carry_on_from()
    {
        for (var minute = 0; minute < 30; minute++)
        {
            Write(Pass(minute));
        }

        var page = Reader.Index(new CyclePassQuery(Most: 10));

        Assert.Equal(10, page.Rows.Count);
        Assert.False(page.Exhausted);
        Assert.Equal(10, page.Resume);

        // And the cursor walks: the next page starts where this one stopped
        // looking, and never repeats a row.
        var next = Reader.Index(new CyclePassQuery(Skip: page.Resume, Most: 10));

        Assert.Equal(10, next.Rows.Count);
        Assert.All(next.Rows, row => Assert.DoesNotContain(row.At, page.Rows.Select(seen => seen.At)));
    }

    [Fact]
    public void A_read_that_reached_the_end_says_so_rather_than_offering_more()
    {
        Write(Pass(minutes: 0));
        Write(Pass(minutes: 10));

        var page = Reader.Index(new CyclePassQuery(Most: 50));

        Assert.Equal(2, page.Rows.Count);
        Assert.True(page.Exhausted);
    }

    [Fact]
    public void A_read_that_gave_up_looking_is_told_apart_from_one_that_found_everything()
    {
        // The pair of answers that look identical: twelve passes with
        // problems, and twelve in the however-many I got through before I
        // stopped. Reporting only the first is the silent truncation this
        // whole record exists to end.
        for (var minute = 0; minute < CycleLogReader.MostRecordsScanned + 50; minute++)
        {
            Write(Pass(minute));
        }

        var page = Reader.Index(new CyclePassQuery(ProblemsOnly: true, Most: 50));

        Assert.Empty(page.Rows);
        Assert.False(page.Exhausted);
        Assert.Equal(CycleLogReader.MostRecordsScanned, page.Scanned);
        Assert.Equal(CycleLogReader.MostRecordsScanned, page.Resume);
    }

    [Fact]
    public void Paging_does_not_drop_a_pass_whose_unit_finished_out_of_order()
    {
        // Units run several at a time and a record is written when its unit
        // FINISHES, while its timestamp is when the unit STARTED -- so a long
        // unit's record sits below records that started after it. A cursor
        // that paged by "older than this moment" would skip exactly those, at
        // every page boundary, silently.
        //
        // Written newest-completion-first, as the log has them: the long unit
        // started earliest and is written first, so reading backwards meets it
        // last.
        Write(Pass(minutes: 0, unit: "C:\\Vendor\\slow"));
        Write(Pass(minutes: 30, unit: "C:\\Vendor\\a"));
        Write(Pass(minutes: 20, unit: "C:\\Vendor\\b"));
        Write(Pass(minutes: 10, unit: "C:\\Vendor\\c"));

        var first = Reader.Index(new CyclePassQuery(Most: 2));
        var second = Reader.Index(new CyclePassQuery(Skip: first.Resume, Most: 2));

        // All four, once each. The one that started first and finished last is
        // the one a timestamp cursor loses.
        Assert.Equal(
            ["C:\\Vendor\\a", "C:\\Vendor\\b", "C:\\Vendor\\c", "C:\\Vendor\\slow"],
            first.Rows.Concat(second.Rows).Select(row => row.Unit).Order());
    }

    // ---------- one pass, by its natural key ----------

    [Fact]
    public void A_pass_is_fetched_by_when_it_started_and_the_folder_it_walked()
    {
        Write(Pass(minutes: 0));
        Write(Pass(minutes: 10, files: 3));

        var found = Reader.One(TestClock.Start.AddMinutes(10), "C:\\Vendor\\dump");

        Assert.NotNull(found);
        Assert.Equal(3, found.Files.Count);
    }

    [Fact]
    public void Two_units_passing_at_the_same_instant_are_told_apart_by_their_folder()
    {
        // Units are disjoint by folder -- that is what grouping stations by
        // the folders they share is for -- so the folder is what makes the
        // key unique when a tick starts several at once.
        Write(Pass(minutes: 0, unit: "C:\\Vaisala\\dump"));
        Write(Pass(minutes: 0, unit: "D:\\Adcon\\export", uploaded: 7));

        var adcon = Reader.One(TestClock.Start, "D:\\Adcon\\export");

        Assert.NotNull(adcon);
        Assert.Equal(7, adcon.Stations.Sum(station => station.Uploaded));
    }

    [Fact]
    public void A_pass_whose_unit_finished_out_of_order_is_still_found()
    {
        // The same hazard, on the detail fetch. Stopping the search at the
        // first record older than the one wanted would report a pass that is
        // sitting in the file as evicted.
        Write(Pass(minutes: 0, unit: "C:\\Vendor\\slow", files: 5));
        Write(Pass(minutes: 30, unit: "C:\\Vendor\\a"));
        Write(Pass(minutes: 20, unit: "C:\\Vendor\\b"));

        var found = Reader.One(TestClock.Start, "C:\\Vendor\\slow");

        Assert.NotNull(found);
        Assert.Equal(5, found.Files.Count);
    }

    [Fact]
    public void A_pass_evicted_since_the_row_was_drawn_answers_with_nothing()
    {
        Write(Pass(minutes: 0));

        // An ordinary Tuesday for a window left open on a machine working
        // through a backlog.
        Assert.Null(Reader.One(TestClock.Start.AddMinutes(10), "C:\\Vendor\\dump"));
    }

    // ---------- fixtures ----------

    private static CycleStationRecord Station(
        long id, string name, int uploaded = 0, int failed = 0, string? error = null) => new()
    {
        StationLinkId = id,
        Station = name,
        Scanned = 96,
        Held = 1,
        Offered = uploaded + failed,
        Wanted = uploaded + failed,
        Uploaded = uploaded,
        Failed = failed,
        Backlog = failed,
        Error = error,
    };

    private static CycleRecord Pass(
        int minutes,
        long stationLinkId = 11,
        string unit = "C:\\Vendor\\dump",
        string? trigger = null,
        bool completed = true,
        int uploaded = 1,
        int failed = 0,
        int files = 1,
        string? error = null) => new()
    {
        At = TestClock.Start.AddMinutes(minutes),
        Seconds = 4.2,
        Unit = unit,
        Trigger = trigger ?? CycleTriggers.Scheduled,
        Completed = completed,
        Folders = [new CycleFolderRecord(unit, 812)],
        Stations = [Station(stationLinkId, $"Station {stationLinkId}", uploaded, failed, error)],
        Files = Enumerable.Range(0, files).Select(index => new CycleFileRecord
        {
            Outcome = FileOutcomes.Uploaded,
            Name = $"GARISSA_{index:D4}.dat",
            Size = 4096,
            StationLinkId = stationLinkId,
            Count = 1,
        }).ToList(),
    };

    private void Write(CycleRecord record)
    {
        Directory.CreateDirectory(_folder);

        File.AppendAllText(
            Path.Combine(_folder, $"{AgentLogs.CycleLogName}-20260821{CycleLog.Extension}"),
            System.Text.Json.JsonSerializer.Serialize(record, Core.Serialization.AgentJson.Options) + "\n");
    }
}
