using AdlAgent.Core.Diagnostics;
using AdlAgent.TestSupport;
using AdlAgent.Tray;
using AdlAgent.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Reading the record at the machine: the passes a station has been in, the
/// bundle somebody emails, and the general log that until now did not exist.
/// </summary>
/// <remarks>
/// Driven over the real transport, the same distance as the rest of the local
/// UI, because the arrangement being tested is exactly the one a substitute
/// would hide: the records live in a folder whose permissions are SYSTEM and
/// Administrators, and everything the tray knows about them it knows because
/// the service answered.
/// </remarks>
public class DiagnosticsTests
{
    private const string Garissa = "C:\\VendorData\\Garissa";

    // ---------- recent passes, at the machine ----------

    [Fact]
    public async Task Check_status_lists_the_passes_this_station_has_been_in()
    {
        await using var shown = await Collecting();
        var window = shown.Window;

        window.SelectedConnection = window.Connections[0];
        window.SelectedStation = window.SelectedConnection!.Stations[0];

        var status = window.BeginWatching();

        Assert.NotNull(status);

        await status.CheckAsync();

        var pass = Assert.Single(status.Passes);

        // Headings only here: the at-a-glance half of the question. The file
        // detail is one click away, in the window this row's View more opens.
        Assert.Equal(Garissa, pass.Folder);

        // A machine's first pass over a station is that station's first
        // sweep, which is what the trigger says rather than "scheduled".
        Assert.Equal("sweep", pass.Trigger);
        Assert.Equal(1, pass.Uploaded);
        Assert.False(pass.Problem);

        Assert.True(status.HasPasses);
        Assert.Equal("", status.PassesMessage);
        Assert.Equal(11, status.StationLinkId);
    }

    [Fact]
    public async Task A_station_with_no_recorded_pass_is_told_so_rather_than_shown_a_blank()
    {
        await using var shown = await Collecting(collect: false);
        var window = shown.Window;

        window.SelectedConnection = window.Connections[0];
        window.SelectedStation = window.SelectedConnection!.Stations[0];

        var status = window.BeginWatching()!;

        await status.CheckAsync();

        // The empty box is the thing this whole record exists to stop being
        // the answer.
        Assert.False(status.HasPasses);
        Assert.Contains("not recorded a collection pass", status.PassesMessage);
    }

    [Fact]
    public async Task The_passes_a_UI_is_given_fit_in_one_control_message()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));

        // Enough passes, each with enough file detail, that the answer would
        // not fit in one line of the control protocol.
        for (var file = 0; file < 60; file++)
        {
            agent.Files.Add(
                Garissa,
                $"GARISSA_{file:D4}.dat",
                agent.Time.GetUtcNow() - TimeSpan.FromHours(file + 1),
                new string('x', 64));
        }

        await agent.PairAsync();

        for (var pass = 0; pass < 25; pass++)
        {
            await agent.Cycle.RunAsync();
            agent.Time.Advance(TimeSpan.FromMinutes(10));
        }

        await agent.CycleLog.FlushAsync();

        using var serving = await ServedAgent.ServingAsync(agent);

        var answer = await serving.Link.PassesAsync(
            new CyclePassQuery(Most: PassesViewModel.Page));

        // Answered rather than refused, which is the point: the reader at the
        // other end rejects a line over the cap, and a window that showed
        // nothing at all on the busiest machine would be the worst outcome.
        Assert.True(answer.Ok);
        Assert.Equal(25, answer.Value!.Rows.Count);

        // And this is what rows are for: twenty-five passes carrying sixty
        // files each go over in one message, where twenty-five records could
        // not.
        Assert.True(answer.Value.Exhausted);
    }

    // ---------- the bundle ----------

    [Fact]
    public async Task Save_diagnostics_writes_what_this_machine_knows_about_itself()
    {
        await using var shown = await Collecting();

        var path = Path.Combine(
            Directory.CreateTempSubdirectory("adl-agent-bundle").FullName, "diagnostics.txt");

        try
        {
            await shown.Window.SaveDiagnosticsAsync(path);

            Assert.Contains("Diagnostics saved to", shown.Window.Message);

            var bundle = await File.ReadAllTextAsync(path);

            // What somebody at HQ opens: what this machine is, what its
            // stations are, and what it has actually been doing.
            Assert.Contains("ADL Agent diagnostics", bundle);
            Assert.Contains("Stations", bundle);
            Assert.Contains(Garissa, bundle);
            Assert.Contains("Recent collection passes", bundle);
            Assert.Contains("GARISSA_20260821.dat", bundle);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task A_bundle_carries_the_bundles_own_number_of_passes_not_a_windows_page_size()
    {
        // The filter that travels with the path is a window's, and a window's
        // Most is its page size. Borrowing it once quietly cut every bundle
        // from two hundred passes to fifty.
        await using var shown = await Collecting();

        var path = Path.Combine(
            Directory.CreateTempSubdirectory("adl-agent-bundle").FullName, "diagnostics.txt");

        try
        {
            await shown.Window.SaveDiagnosticsAsync(path);

            Assert.Contains(
                $"at most {DiagnosticsBundle.Passes}",
                await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task A_bundle_the_agent_cannot_write_is_a_sentence_rather_than_a_crash()
    {
        await using var shown = await Collecting(collect: false);

        // A path the service account does not have, which is an everyday case:
        // the service runs as SYSTEM and the path came from somebody's Save
        // dialog on a mapped drive.
        await shown.Window.SaveDiagnosticsAsync(
            Path.Combine(Path.GetTempPath(), "adl-agent-missing", "\0", "bundle.txt"));

        Assert.Contains("could not", shown.Window.Message);
    }

    // ---------- the general log ----------

    [Fact]
    public async Task ILogger_output_reaches_a_file_on_a_machine_with_no_event_log_worth_reading()
    {
        var folder = Directory.CreateTempSubdirectory("adl-agent-general").FullName;

        try
        {
            using var provider = new AgentFileLoggerProvider(
                folder,
                AgentLogs.GeneralLogMegabytesDefault,
                LogLevel.Information,
                new FakeTimeProvider(TestClock.Start));

            provider.CreateLogger("AdlAgent.Core.Cycle.UploadCycle")
                .LogWarning("The manifest did not reach ADL: {Reason}", "the network path was not found");

            await provider.FlushAsync();

            var written = await File.ReadAllTextAsync(
                Path.Combine(folder, $"{AgentLogs.GeneralLogName}-20260821{AgentFileLoggerProvider.Extension}"));

            Assert.Contains("WARN", written);
            Assert.Contains("UploadCycle", written);
            Assert.Contains("the network path was not found", written);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task An_exception_reaches_the_file_in_full_because_that_is_what_anybody_opens_it_for()
    {
        var folder = Directory.CreateTempSubdirectory("adl-agent-general").FullName;

        try
        {
            using var provider = new AgentFileLoggerProvider(
                folder, 32, LogLevel.Information, new FakeTimeProvider(TestClock.Start));

            provider.CreateLogger("AdlAgent.Core.Control.AgentControlService")
                .LogError(new InvalidOperationException("the pipe was already taken"), "The control surface stopped serving.");

            await provider.FlushAsync();

            var written = await File.ReadAllTextAsync(
                Path.Combine(folder, $"{AgentLogs.GeneralLogName}-20260821{AgentFileLoggerProvider.Extension}"));

            Assert.Contains("ERROR", written);
            Assert.Contains("InvalidOperationException", written);
            Assert.Contains("the pipe was already taken", written);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task What_is_written_follows_the_level_the_settings_file_asks_for()
    {
        var folder = Directory.CreateTempSubdirectory("adl-agent-general").FullName;

        try
        {
            using var quiet = new AgentFileLoggerProvider(
                folder, 32, LogLevel.Information, new FakeTimeProvider(TestClock.Start));

            quiet.CreateLogger("AdlAgent.Core.Cycle.UploadCycle").LogDebug("ADL took a file.");

            await quiet.FlushAsync();

            var path = Path.Combine(
                folder, $"{AgentLogs.GeneralLogName}-20260821{AgentFileLoggerProvider.Extension}");

            Assert.False(File.Exists(path));

            using var talkative = new AgentFileLoggerProvider(
                folder, 32, LogLevel.Debug, new FakeTimeProvider(TestClock.Start));

            talkative.CreateLogger("AdlAgent.Core.Cycle.UploadCycle").LogDebug("ADL took a file.");

            await talkative.FlushAsync();

            Assert.Contains("ADL took a file.", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_machine_whose_settings_file_asks_for_Debug_actually_gets_Debug()
    {
        // Through the real host, which is the only place this can be told.
        // The provider has a minimum of its own, but it is not the thing that
        // decides: the framework's filter pipeline runs first, and this
        // agent ships an appsettings.json with a Logging section in it.
        var state = Directory.CreateTempSubdirectory("adl-agent-level").FullName;

        try
        {
            using var host = Windows.WindowsAgentHost.CreateBuilder([
                "--Agent:AdlBaseUrl=https://adl.example.org",
                $"--Agent:StateDirectory={state}",
                "--Agent:LogLevel=Debug",
            ]).Build();

            host.Services.GetRequiredService<ILogger<DiagnosticsTests>>()
                .LogDebug("A support session asked for this.");

            foreach (var log in host.Services.GetServices<ILogFlush>())
            {
                await log.FlushAsync();
            }

            Assert.Contains(
                "A support session asked for this.",
                await File.ReadAllTextAsync(Newest(AgentLogs.In(state))));
        }
        finally
        {
            Directory.Delete(state, recursive: true);
        }
    }

    [Fact]
    public async Task A_machine_that_asks_for_nothing_is_not_told_about_every_file_ADL_takes()
    {
        var state = Directory.CreateTempSubdirectory("adl-agent-level").FullName;

        try
        {
            using var host = Windows.WindowsAgentHost.CreateBuilder([
                "--Agent:AdlBaseUrl=https://adl.example.org",
                $"--Agent:StateDirectory={state}",
            ]).Build();

            var logger = host.Services.GetRequiredService<ILogger<DiagnosticsTests>>();

            logger.LogDebug("ADL took a file.");
            logger.LogWarning("The manifest did not reach ADL.");

            foreach (var log in host.Services.GetServices<ILogFlush>())
            {
                await log.FlushAsync();
            }

            var written = await File.ReadAllTextAsync(Newest(AgentLogs.In(state)));

            Assert.DoesNotContain("ADL took a file.", written);
            Assert.Contains("The manifest did not reach ADL.", written);
        }
        finally
        {
            Directory.Delete(state, recursive: true);
        }
    }

    private static string Newest(string folder) =>
        Directory.EnumerateFiles(folder).OrderByDescending(File.GetLastWriteTimeUtc).First();

    [Fact]
    public void The_ceilings_a_settings_file_asks_for_are_the_ceilings_the_logs_hold()
    {
        // Through the real host and the real INI reader, because "settable in
        // MachineSettings" is a claim about a file an administrator edits over
        // a telephone -- and a key named in the code but never read would look
        // exactly like one that works.
        var state = Directory.CreateTempSubdirectory("adl-agent-ceilings").FullName;

        try
        {
            File.WriteAllText(
                Path.Combine(state, MachineSettings.FileName),
                string.Join(
                    "\r\n",
                    $"[{MachineSettings.Section}]",
                    $"{MachineSettings.AdlBaseUrlKey}=https://adl.example.org",
                    $"{MachineSettings.CycleLogMegabytesKey}=8",
                    $"{MachineSettings.GeneralLogMegabytesKey}=4",
                    ""));

            var configuration = new ConfigurationBuilder()
                .AddIniFile(MachineSettings.PathIn(state))
                .Build();

            var options = new AgentOptions();

            configuration.GetSection(AgentOptions.SectionName).Bind(options);

            Assert.Equal(8, options.CycleLogMegabytes);
            Assert.Equal(4, options.GeneralLogMegabytes);
        }
        finally
        {
            Directory.Delete(state, recursive: true);
        }
    }

    [Fact]
    public void A_machine_says_nothing_about_its_logs_and_gets_the_defaults()
    {
        // Information, and two ceilings that between them are one sentence a
        // ministry's system administrator can be given.
        var options = new AgentOptions();

        Assert.Equal("Information", options.LogLevel);
        Assert.Equal(64, options.CycleLogMegabytes);
        Assert.Equal(32, options.GeneralLogMegabytes);
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
                    3, "Vaisala AWS", stationLinks: [SyncConfigs.Link(11, Garissa, "*.dat")]));

            agent.Files.Add(
                Garissa,
                "GARISSA_20260821.dat",
                agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5),
                "09:00,21.4\n");

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
