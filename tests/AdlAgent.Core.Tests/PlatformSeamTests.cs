using AdlAgent.Core.Platform;
using AdlAgent.TestSupport;
using AdlAgent.Windows.Platform;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The four seams: the real providers where they can be run, and the fakes
/// where the interesting cases belong to another operating system.
/// </summary>
public class PlatformSeamTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("adl-agent-seams").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_metadata_seam_describes_a_file_by_name_size_and_one_timestamp()
    {
        var path = Write("GARISSA_20260821.dat", "0,1,2\n");
        var source = new WindowsFileMetadataSource();

        var facts = source.Describe(_folder, "GARISSA_20260821.dat");

        Assert.NotNull(facts);
        Assert.Equal("GARISSA_20260821.dat", facts.Value.Name);
        Assert.Equal(new FileInfo(path).Length, facts.Value.Length);
        Assert.True(facts.Value.WindowTimestamp >= File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void A_file_that_is_not_there_is_not_an_error()
    {
        var source = new WindowsFileMetadataSource();

        Assert.Null(source.Describe(_folder, "never-written.dat"));
        Assert.Null(source.Describe(Path.Combine(_folder, "no-such-folder"), "never-written.dat"));
        Assert.Null(source.Describe(_folder, ""));
        Assert.Empty(source.Enumerate(Path.Combine(_folder, "no-such-folder")));
        Assert.Empty(source.Enumerate(""));
    }

    [Fact]
    public void A_name_the_filesystem_could_not_hold_describes_nothing()
    {
        Write("GARISSA_20260821.dat", "0,1,2\n");

        var source = new WindowsFileMetadataSource();

        // A station's filename datetime format is free text in ADL, and one
        // of the formats the FTP plugin offers is YYYY-MM-DDTHH:MM:SS -- a
        // perfectly good name on the Unix servers it was written for. On
        // Windows the colon names an alternate data stream of the folder
        // itself, so this has to answer "not there" rather than open
        // something.
        Assert.Null(source.Describe(_folder, "GARISSA_2026-08-21T09:00:00.dat"));

        // And a name that climbs out of the folder it was given never
        // reaches the filesystem at all.
        Assert.Null(source.Describe(_folder, Path.Combine("..", "GARISSA_20260821.dat")));
    }

    [Fact]
    public void The_metadata_seam_joins_a_dated_folder_onto_the_one_ADL_named()
    {
        var source = new WindowsFileMetadataSource();

        var dated = source.Descend(_folder, ["2026", "08", "21"]);

        Assert.Equal(Path.Combine(_folder, "2026", "08", "21"), dated);

        // The two ways a technician may have typed the folder join to one
        // string, because that string is also the key two stations sharing a
        // dated tree are grouped under -- and two spellings would have the
        // tree walked twice.
        Assert.Equal(dated, source.Descend(_folder + Path.DirectorySeparatorChar, ["2026", "08", "21"]));

        // A segment that looks rooted stays a segment. Path.Combine would
        // throw the folder away and answer with the drive root, which is a
        // station reading a filesystem nobody pointed it at.
        Assert.Equal(
            Path.Combine(_folder, "2026"),
            source.Descend(_folder, [Path.DirectorySeparatorChar + "2026"]));

        Assert.Equal(_folder, source.Descend(_folder, []));
    }

    [Fact]
    public void A_dated_folder_that_is_not_there_yet_enumerates_to_nothing()
    {
        Write("GARISSA_20260821.dat", "0,1,2\n");

        var source = new WindowsFileMetadataSource();

        // The vendor has not written today's folder. Not an error and not an
        // exception: every station filed by day is in this state for part of
        // every day.
        Assert.Empty(source.Enumerate(source.Descend(_folder, ["2026", "08", "21"])));
    }

    [Fact]
    public void The_metadata_seam_streams_rather_than_listing()
    {
        Write("one.dat", "1");

        var source = new WindowsFileMetadataSource();
        var files = source.Enumerate(_folder);

        // Written after Enumerate was called and before it was walked. A
        // provider that had materialised the listing could not see it -- and
        // on the folders this product exists for, materialising is the thing
        // that must never happen.
        Write("two.dat", "2");

        Assert.Equal(2, files.Count());
        Assert.Contains(files, file => file.Name == "two.dat");
    }

    [Fact]
    public void The_readiness_seam_leaves_a_file_that_was_just_written_alone()
    {
        Write("still-being-written.dat", "half a line");

        var facts = new WindowsFileMetadataSource().Describe(_folder, "still-being-written.dat")!.Value;
        var probe = new WindowsFileReadinessProbe();

        var justWritten = facts.WindowTimestamp + TimeSpan.FromSeconds(10);

        Assert.False(probe.IsReadyToRead(facts, TimeSpan.FromSeconds(60), justWritten));

        var settled = facts.WindowTimestamp + TimeSpan.FromSeconds(120);

        Assert.True(probe.IsReadyToRead(facts, TimeSpan.FromSeconds(60), settled));
    }

    [Fact]
    public void The_readiness_seam_leaves_a_file_someone_else_is_holding_alone()
    {
        var path = Write("held-open.dat", "vendor software is still writing this");
        var facts = new WindowsFileMetadataSource().Describe(_folder, "held-open.dat")!.Value;
        var probe = new WindowsFileReadinessProbe();
        var settled = facts.WindowTimestamp + TimeSpan.FromSeconds(120);

        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            Assert.False(probe.IsReadyToRead(facts, TimeSpan.FromSeconds(60), settled));
        }

        Assert.True(probe.IsReadyToRead(facts, TimeSpan.FromSeconds(60), settled));
    }

    [Fact]
    public void The_host_seam_keeps_state_where_a_service_is_allowed_to_write()
    {
        var host = new WindowsHostLifecycle(TimeProvider.System);

        Assert.EndsWith(WindowsHostLifecycle.StateFolderName, host.StateDirectory);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            host.StateDirectory);
        Assert.False(string.IsNullOrWhiteSpace(host.PlatformDescription));
    }

    [Fact]
    public void A_backfilled_file_is_windowed_on_when_it_arrived_not_when_it_was_written()
    {
        // Story 15, as the platforms see it. A file recovered from a logger
        // and copied into the folder today keeps last week's last-write time.
        var lastWrite = DateTimeOffset.Parse("2026-08-01T06:00:00Z");
        var arrived = DateTimeOffset.Parse("2026-08-21T09:00:00Z");
        var watermark = DateTimeOffset.Parse("2026-08-15T00:00:00Z");

        // Windows: creation time is when the copy landed, so the file is
        // above the watermark and gets offered.
        Assert.True(PlatformWindowing.WindowsLike(lastWrite, arrived) > watermark);

        // Linux with birth time: the same answer, from statx.
        Assert.True(PlatformWindowing.LinuxLike(lastWrite, arrived) > watermark);

        // Linux without it: the file looks three weeks old, and only the
        // reconciliation sweep will find it. Stated here because it is the
        // difference the Linux head has to be built knowing about.
        Assert.True(PlatformWindowing.LinuxLike(lastWrite) < watermark);
    }

    [Fact]
    public void A_locked_file_is_a_Windows_observation_and_a_Linux_blind_spot()
    {
        var file = new FileFacts("C:\\VendorData\\Garissa\\open.dat", "open.dat", 10,
            DateTimeOffset.Parse("2026-08-21T08:00:00Z"));
        var now = DateTimeOffset.Parse("2026-08-21T09:00:00Z");
        var window = TimeSpan.FromSeconds(60);

        var windows = new FakeFileReadinessProbe { ObservesLocks = true };

        windows.LockedPaths.Add(file.Path);

        Assert.False(windows.IsReadyToRead(file, window, now));

        // On Linux an open file says nothing about whether anyone is writing
        // to it, so the stability window is the whole answer -- which is why
        // this judgement lives behind a seam and not behind a flag.
        var linux = new FakeFileReadinessProbe { ObservesLocks = false };

        Assert.True(linux.IsReadyToRead(file, window, now));
        Assert.False(linux.IsReadyToRead(file, window, file.WindowTimestamp.AddSeconds(30)));
    }

    private string Write(string name, string contents)
    {
        var path = Path.Combine(_folder, name);

        File.WriteAllText(path, contents);

        return path;
    }
}
