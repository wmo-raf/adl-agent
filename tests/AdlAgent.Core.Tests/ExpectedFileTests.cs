using AdlAgent.Core.Api;
using AdlAgent.Core.Cycle;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The filenames a DIRECT_FETCH station goes looking for.
/// </summary>
/// <remarks>
/// Tested here rather than only at the cycle seam because this is the one
/// part of the strategy with no feedback: an enumerating station that has its
/// pattern wrong is told how many files it looked at and matched none of, but
/// a station building names in the wrong timezone finds nothing and has
/// nothing to compare that against. Every rule below is one where being
/// wrong means a station quietly collects nothing.
/// </remarks>
public class ExpectedFileTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-21T09:00:00Z");

    [Fact]
    public void The_names_run_newest_first_from_the_interval_now_is_in()
    {
        var expected = ExpectedFiles.For(
            Config(intervalMinutes: 10, format: "yyyyMMddHHmm", timezone: "UTC"),
            floor: Now - TimeSpan.FromMinutes(30),
            Now,
            ExpectedFiles.MostPerCycle);

        Assert.Null(expected.Problem);

        // Newest first, so a fresh install's first page is today's files
        // (story 18). The oldest is the interval the floor falls in and not
        // the one after it: a file named for 08:30 holds the ten minutes
        // after 08:30, which is what a floor of 08:30 is asking for.
        Assert.Equal(
            [
                "GARISSA_202608210900.dat",
                "GARISSA_202608210850.dat",
                "GARISSA_202608210840.dat",
                "GARISSA_202608210830.dat",
            ],
            expected.Names);
    }

    [Fact]
    public void The_name_is_written_in_the_station_timezone_and_not_this_machine_s()
    {
        var expected = ExpectedFiles.For(
            Config(intervalMinutes: 60, format: "yyyyMMdd_HHmm", timezone: "Africa/Nairobi"),
            floor: Now - TimeSpan.FromMinutes(30),
            Now,
            ExpectedFiles.MostPerCycle);

        // 09:00 UTC is noon in Nairobi, and noon is what the vendor writes.
        // An agent that built this name in UTC would look for a file three
        // hours from the one on the disk, find nothing, and go on finding
        // nothing for ever.
        Assert.Equal(
            ["GARISSA_20260821_1200.dat", "GARISSA_20260821_1100.dat"],
            expected.Names);

        Assert.DoesNotContain("GARISSA_20260821_0900.dat", expected.Names);
    }

    [Fact]
    public void The_interval_grid_is_the_one_the_vendor_sees()
    {
        var expected = ExpectedFiles.For(
            Config(intervalMinutes: 10, format: "HHmm", timezone: "Asia/Kathmandu"),
            floor: Now - TimeSpan.FromMinutes(20),
            Now,
            ExpectedFiles.MostPerCycle);

        // Kathmandu is three quarters of an hour off the hour, so a logger
        // writing every ten minutes writes 14:40, 14:30 -- not 14:45, 14:35.
        // Aligning on the Unix epoch instead of on local midnight would miss
        // every file this station has, in every country on a fractional
        // offset.
        Assert.Equal(
            ["GARISSA_1440.dat", "GARISSA_1430.dat", "GARISSA_1420.dat"], expected.Names);
    }

    [Fact]
    public void A_coarse_format_names_one_file_however_often_the_logger_writes()
    {
        var expected = ExpectedFiles.For(
            Config(intervalMinutes: 60, format: "yyyyMMdd", timezone: "UTC"),
            floor: Now - TimeSpan.FromHours(26),
            Now,
            ExpectedFiles.MostPerCycle);

        // A logger appending to one file a day, checked hourly: twenty-seven
        // instants, two filenames. Offering the same name twenty-seven times
        // would fill a manifest page with one file.
        Assert.Equal(["GARISSA_20260821.dat", "GARISSA_20260820.dat"], expected.Names);
    }

    [Fact]
    public void A_floor_ADL_never_set_leaves_the_bound_as_the_only_one()
    {
        var expected = ExpectedFiles.For(
            Config(intervalMinutes: 10, format: "yyyyMMddHHmm", timezone: "UTC"),
            floor: null,
            Now,
            ExpectedFiles.MostPerCycle);

        Assert.True(expected.Truncated);
        Assert.Equal(ExpectedFiles.MostPerCycle, expected.Names.Count);

        // And what survived the bound is the newest end of it.
        Assert.Equal("GARISSA_202608210900.dat", expected.Names[0]);
    }

    [Fact]
    public void A_floor_within_reach_is_not_a_truncation()
    {
        var expected = ExpectedFiles.For(
            Config(intervalMinutes: 10, format: "yyyyMMddHHmm", timezone: "UTC"),
            floor: Now - TimeSpan.FromHours(1),
            Now,
            ExpectedFiles.MostPerCycle);

        Assert.False(expected.Truncated);
        Assert.Equal(7, expected.Names.Count);
    }

    [Theory]
    [InlineData(null, "yyyyMMddHHmm", "UTC", "file interval")]
    [InlineData(0, "yyyyMMddHHmm", "UTC", "file interval")]
    [InlineData(10, null, "UTC", "datetime format")]
    [InlineData(10, "", "UTC", "datetime format")]
    [InlineData(10, "yyyyMMddHHmm", "Mars/Olympus", "does not know the timezone")]
    // A single character is a standard format specifier to .NET, and not one
    // it knows. A technician who typed one gets a sentence, not a station
    // that looks for nothing.
    [InlineData(10, "!", "UTC", "not a filename datetime format")]
    // Every separator in the format would send the agent below the folder
    // ADL named it. What a station may read is an administrator's decision.
    [InlineData(10, "yyyy/MM/dd", "UTC", "folder separator")]
    public void A_station_that_cannot_build_a_name_says_why_and_builds_none(
        int? interval, string? format, string timezone, string expectedProblem)
    {
        var expected = ExpectedFiles.For(
            Config(interval, format, timezone), floor: null, Now, ExpectedFiles.MostPerCycle);

        Assert.Empty(expected.Names);
        Assert.Contains(expectedProblem, expected.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_station_ADL_gave_no_timezone_is_read_as_UTC()
    {
        // An older instance, or a field left empty. UTC is the only reading
        // that is a reading rather than a guess.
        var expected = ExpectedFiles.For(
            Config(intervalMinutes: 60, format: "HHmm", timezone: ""),
            floor: Now - TimeSpan.FromMinutes(30),
            Now,
            ExpectedFiles.MostPerCycle);

        Assert.Null(expected.Problem);
        Assert.Equal(["GARISSA_0900.dat", "GARISSA_0800.dat"], expected.Names);
    }

    private static StationLinkAppConfig Config(
        int? intervalMinutes, string? format, string timezone) => new()
    {
        LocalFolderPath = "C:\\VendorData\\All",
        ListingStrategy = ListingStrategies.DirectFetch,
        DirectFetchPrefix = "GARISSA_",
        DirectFetchIntervalMinutes = intervalMinutes,
        DirectFetchDatetimeFormat = format,
        DirectFetchDatetimeTimezone = timezone,
        DirectFetchFileExtension = ".dat",
    };
}
