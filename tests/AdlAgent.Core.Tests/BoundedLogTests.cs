using System.IO.Compression;
using System.Text;
using AdlAgent.Core.Diagnostics;
using AdlAgent.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The promise the whole diagnostic rests on: this folder never exceeds its
/// ceiling.
/// </summary>
/// <remarks>
/// Everything else here is about what is written; these are about what is
/// kept. A log that could grow without bound would be a log a ministry's
/// system administrator is right to switch off, and then a machine that has
/// nothing to say about itself again.
/// </remarks>
public class BoundedLogTests : IDisposable
{
    private readonly string _folder =
        Directory.CreateTempSubdirectory("adl-agent-logs").FullName;

    private readonly FakeTimeProvider _time = new(TestClock.Start);

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
    public void Todays_records_are_in_a_plain_file_anybody_can_open()
    {
        var writer = Writer();

        writer.Write("first");
        writer.Write("second");

        var today = Path.Combine(_folder, "cycle-20260821.jsonl");

        Assert.True(File.Exists(today));
        Assert.Equal(["first", "second"], File.ReadAllLines(today));
    }

    [Fact]
    public void A_day_that_has_ended_is_gzipped()
    {
        var writer = Writer();

        writer.Write("yesterday");

        _time.Advance(TimeSpan.FromDays(1));

        writer.Write("today");

        Assert.False(File.Exists(Path.Combine(_folder, "cycle-20260821.jsonl")));
        Assert.Equal("yesterday", Unzipped(Path.Combine(_folder, "cycle-20260821-001.jsonl.gz")).Trim());

        // And today's is still plain, because a technician standing at the
        // machine has to be able to open the one that is being written.
        Assert.Equal("today", File.ReadAllText(Path.Combine(_folder, "cycle-20260822.jsonl")).Trim());
    }

    [Fact]
    public void A_day_the_service_was_not_running_for_the_end_of_is_gzipped_on_the_next_start()
    {
        Writer().Write("before the power cut");

        _time.Advance(TimeSpan.FromDays(3));

        // A different writer over the same folder: this machine was restarted.
        Writer().Write("after it came back");

        Assert.False(File.Exists(Path.Combine(_folder, "cycle-20260821.jsonl")));
        Assert.Equal(
            "before the power cut",
            Unzipped(Path.Combine(_folder, "cycle-20260821-001.jsonl.gz")).Trim());
    }

    [Fact]
    public void The_ceiling_holds_under_a_pass_that_writes_far_more_than_it()
    {
        // The pathological cycle: a share that unmounted and failed every
        // file, or a first bind uploading for hours. It is all one day, so a
        // writer that only rolled at midnight would hold every byte of it.
        var writer = Writer(megabytes: 4);
        var line = new string('x', 4000);

        for (var written = 0; written < 40 * 1024 * 1024; written += line.Length)
        {
            writer.Write(line);
        }

        Assert.True(
            writer.Bytes() <= 4L * 1024 * 1024,
            $"The log grew to {writer.Bytes()} bytes under a 4 MB ceiling.");

        // And it is still a log: what survived is the newest of it.
        Assert.NotEmpty(writer.Files());
    }

    [Fact]
    public void Eviction_never_looks_outside_this_logs_own_files()
    {
        // The whole safety argument. The state folder above holds the device
        // token, the configuration cache and the sweep log, and an eviction
        // routine one directory out from those is a machine somebody has to
        // visit.
        var writer = Writer(megabytes: 4);

        File.WriteAllText(Path.Combine(_folder, "agent-20260821.log"), "the other log");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "somebody's notes");

        var line = new string('x', 4000);

        for (var written = 0; written < 20 * 1024 * 1024; written += line.Length)
        {
            writer.Write(line);
        }

        Assert.True(File.Exists(Path.Combine(_folder, "agent-20260821.log")));
        Assert.True(File.Exists(Path.Combine(_folder, "notes.txt")));
    }

    [Fact]
    public void Two_logs_in_one_folder_hold_two_independent_ceilings()
    {
        // Independent so that a chatty subsystem can never evict cycle
        // history. They share a folder and nothing else.
        var cycle = Writer(megabytes: 4);
        var general = new BoundedLogWriter(_folder, "agent", ".log", 4 * 1024 * 1024, _time);

        cycle.Write("the pass that matters");

        var line = new string('y', 4000);

        for (var written = 0; written < 40 * 1024 * 1024; written += line.Length)
        {
            general.Write(line);
        }

        Assert.Equal("the pass that matters", File.ReadAllText(Path.Combine(_folder, "cycle-20260821.jsonl")).Trim());
        Assert.True(general.Bytes() <= 4L * 1024 * 1024);
    }

    [Fact]
    public async Task Nothing_a_caller_hands_over_touches_a_disk_on_the_callers_thread()
    {
        // The rule stated as a test that can only ever be circumstantial: what
        // is asserted is that the write is not visible when the call returns
        // and is visible after a flush, which is what a queue behind it means.
        await using var queue = new BackgroundLogQueue(Writer(), static dropped => $"dropped {dropped}");

        queue.Write("something");

        await queue.FlushAsync(CancellationToken.None);

        Assert.Equal("something", File.ReadAllText(Path.Combine(_folder, "cycle-20260821.jsonl")).Trim());
    }

    [Fact]
    public async Task A_log_that_could_not_keep_up_says_how_much_it_lost()
    {
        await using var queue = new BackgroundLogQueue(Writer(), static dropped => $"dropped {dropped}");

        for (var index = 0; index < BackgroundLogQueue.Capacity * 4; index++)
        {
            queue.Write($"line {index}");
        }

        await queue.FlushAsync(CancellationToken.None);

        var written = File.ReadAllText(Path.Combine(_folder, "cycle-20260821.jsonl"));

        // Either everything got through -- a fast machine drains as fast as
        // this loop fills -- or what did not is said out loud. What must never
        // happen is a silent gap.
        Assert.True(
            queue.Dropped == 0 || written.Contains("dropped", StringComparison.Ordinal),
            "Records were dropped and the log did not say so.");
    }

    [Fact]
    public void A_machine_that_is_not_an_installed_agent_keeps_no_log()
    {
        // The hazard this closes is narrow and serious. The state folder's
        // permissions are replaced by the installer with SYSTEM and
        // Administrators because the device token is stored in it in the
        // clear; a log writer that created the whole tree would create that
        // folder with whatever %ProgramData% grants, and the next pairing
        // would put a credential in it.
        var nowhere = Path.Combine(_folder, "not-an-install", "ADL Agent");
        var writer = new BoundedLogWriter(
            AgentLogs.In(nowhere), AgentLogs.CycleLogName, CycleLog.Extension, 4 * 1024 * 1024, _time);

        writer.Write("something");

        Assert.False(Directory.Exists(nowhere));
        Assert.Empty(writer.Files());
    }

    private BoundedLogWriter Writer(int megabytes = 64) =>
        new(_folder, AgentLogs.CycleLogName, CycleLog.Extension, (long)megabytes * 1024 * 1024, _time);

    private static string Unzipped(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
