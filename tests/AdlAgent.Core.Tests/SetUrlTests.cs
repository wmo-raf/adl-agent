using AdlAgent.Core.Api;
using AdlAgent.Core.Diagnostics;
using AdlAgent.Core.State;
using Microsoft.Extensions.Configuration;
using AdlAgent.TestSupport;
using AdlAgent.Windows;
using AdlAgent.Windows.Platform;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Repointing a machine at a different ADL, from the machine itself.
/// </summary>
/// <remarks>
/// Driven at the three seams the verb actually has: the settings file it
/// writes, the state store it empties, and the service it restarts. Between
/// them they say everything worth saying about a command whose whole job is
/// to leave a machine in a different state and then get out of the way --
/// including the case that matters most, which is the one where it refuses
/// and must leave no trace of having been run.
/// </remarks>
public class SetUrlTests : IDisposable
{
    private readonly string _stateDirectory =
        Directory.CreateTempSubdirectory("adl-agent-set-url").FullName;

    private readonly InMemoryAgentStateStore _state = new();
    private readonly RecordingServiceControl _service = new();
    private readonly StringWriter _output = new();

    public void Dispose()
    {
        Directory.Delete(_stateDirectory, recursive: true);
        _output.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---------- what gets written ----------

    [Fact]
    public async Task An_address_is_written_to_the_machines_settings_file()
    {
        var exitCode = await Repoint("https://adl.example.org");

        Assert.Equal(0, exitCode);
        Assert.Contains("[Agent]", Settings());
        Assert.Contains("AdlBaseUrl=https://adl.example.org", Settings());
    }

    [Fact]
    public async Task The_new_address_replaces_the_old_one_and_leaves_the_rest_of_the_file_alone()
    {
        Seed("""
             [Agent]
             AdlBaseUrl=https://old.example.org
             AutoUpdate=false
             """);

        Assert.Equal(0, await Repoint("https://new.example.org"));

        var settings = Settings();

        Assert.Contains("AdlBaseUrl=https://new.example.org", settings);
        Assert.DoesNotContain("old.example.org", settings);

        // The other reason a country sets anything in this file. Losing it on
        // a repoint would turn one machine's IT policy off at three in the
        // morning, silently.
        Assert.Contains("AutoUpdate=false", settings);
    }

    [Fact]
    public async Task A_settings_file_with_no_Agent_section_gains_one()
    {
        Seed("""
             ; written by hand
             [Diagnostics]
             Verbose=true
             """);

        Assert.Equal(0, await Repoint("https://adl.example.org"));

        var settings = Settings();

        Assert.Contains("[Diagnostics]", settings);
        Assert.Contains("Verbose=true", settings);
        Assert.Contains("[Agent]", settings);
        Assert.Contains("AdlBaseUrl=https://adl.example.org", settings);
    }

    [Fact]
    public async Task The_technician_is_told_which_file_was_written()
    {
        await Repoint("https://adl.example.org");

        Assert.Contains(MachineSettings.PathIn(_stateDirectory), _output.ToString());
    }

    [Fact]
    public async Task A_key_written_with_spaces_around_it_is_the_key_that_gets_replaced()
    {
        // What a person editing this file by hand produces, and what the
        // configuration reader itself accepts. A repoint that missed it would
        // leave two AdlBaseUrl lines and let the first one win.
        Seed("""
             [agent]
             AdlBaseUrl = https://old.example.org
             """);

        Assert.Equal(0, await Repoint("https://new.example.org"));

        Assert.DoesNotContain("old.example.org", Settings());
        Assert.Single(
            Settings().Split('\n'),
            line => line.Contains("AdlBaseUrl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_commented_out_address_is_left_as_the_comment_it_is()
    {
        Seed("""
             [Agent]
             ; AdlBaseUrl=https://staging.example.org
             AutoUpdate=false
             """);

        Assert.Equal(0, await Repoint("https://adl.example.org"));

        var settings = Settings();

        Assert.Contains("; AdlBaseUrl=https://staging.example.org", settings);
        Assert.Contains("AdlBaseUrl=https://adl.example.org", settings);
    }

    [Fact]
    public async Task What_is_written_is_what_the_agent_reads_back_at_start_up()
    {
        Seed("""
             [Agent]
             AutoUpdate=false
             """);

        Assert.Equal(0, await Repoint("https://adl.example.org"));

        // The other end of the seam, and the only assertion that proves the
        // file is a file the agent can actually start from: the same INI
        // provider the Windows host adds, binding the same options object.
        var options = new ConfigurationBuilder()
            .AddIniFile(MachineSettings.PathIn(_stateDirectory), optional: false, reloadOnChange: false)
            .Build()
            .GetSection(AgentOptions.SectionName)
            .Get<AgentOptions>();

        Assert.Equal("https://adl.example.org", options!.AdlBaseUrl);
        Assert.False(options.AutoUpdate);
        Assert.Null(options.DescribeConfigurationProblem());
    }

    // ---------- what the machine remembers afterwards ----------

    [Fact]
    public async Task Repointing_a_machine_drops_the_pairing_it_had()
    {
        Pair();

        Assert.Equal(0, await Repoint("https://elsewhere.example.org"));

        var state = _state.Load();

        Assert.Null(state.Token);
        Assert.Null(state.Device);
        Assert.False(state.RePairNeeded);
    }

    [Fact]
    public async Task Repointing_a_machine_forgets_what_the_old_instance_taught_it()
    {
        Pair();

        Assert.Equal(0, await Repoint("https://elsewhere.example.org"));

        // Both belong to the instance this machine has just stopped talking
        // to. The cached configuration would have the tray listing another
        // country's stations on an unpaired machine, and the sweep log is
        // keyed by station link id -- ids the new instance issues to entirely
        // different stations, whose folders would then never be swept.
        Assert.Null(_state.LoadConfig());
        Assert.Empty(_state.LoadSweeps().Swept);
    }

    [Fact]
    public async Task Keeping_the_pairing_leaves_the_token_exactly_where_it_was()
    {
        Pair();

        Assert.Equal(0, await Repoint("https://new-domain.example.org", "--keep-pairing"));

        var state = _state.Load();

        Assert.Equal("device-token", state.Token);
        Assert.Equal("Nairobi vendor server", state.Device!.Name);
        Assert.NotNull(_state.LoadConfig());

        // And the address still moved, which is the whole point of the switch.
        Assert.Contains("AdlBaseUrl=https://new-domain.example.org", Settings());
    }

    // ---------- what a refusal does, which is nothing ----------

    [Theory]
    [InlineData("http://adl.example.org")]
    [InlineData("not a url at all")]
    public async Task An_address_the_agent_would_refuse_is_refused_here_and_nothing_is_written(string url)
    {
        Pair();

        Assert.Equal(1, await Repoint(url));

        Assert.False(File.Exists(MachineSettings.PathIn(_stateDirectory)));
        Assert.Equal("device-token", _state.Load().Token);
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public async Task A_refusal_carries_the_reason_the_agent_itself_would_give()
    {
        Assert.Equal(1, await Repoint("http://adl.example.org"));

        Assert.Contains("https", _output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("device token", _output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refused_address_does_not_disturb_the_settings_that_were_already_there()
    {
        Seed("""
             [Agent]
             AdlBaseUrl=https://old.example.org
             """);

        Assert.Equal(1, await Repoint("http://adl.example.org"));

        Assert.Contains("AdlBaseUrl=https://old.example.org", Settings());
    }

    [Fact]
    public async Task Loopback_is_allowed_here_for_the_same_reason_it_is_allowed_there()
    {
        Assert.Equal(0, await Repoint("http://127.0.0.1:8099"));

        Assert.Contains("AdlBaseUrl=http://127.0.0.1:8099", Settings());
    }

    [Fact]
    public async Task Asking_for_the_verb_without_an_address_explains_itself()
    {
        Assert.Equal(2, await Run(["set-url"]));

        Assert.Contains("Usage: adl-agent set-url", _output.ToString());
        Assert.False(File.Exists(MachineSettings.PathIn(_stateDirectory)));
    }

    [Fact]
    public async Task An_address_that_is_empty_gets_the_reason_rather_than_the_usage_text()
    {
        // "" is an address somebody typed, and the agent has a sentence for a
        // machine that has none. Only the verb with nothing after it at all is
        // somebody asking how to use it.
        Assert.Equal(1, await Run(["set-url", ""]));

        Assert.Contains("No ADL URL is configured", _output.ToString());
        Assert.DoesNotContain("Usage:", _output.ToString());
        Assert.False(File.Exists(MachineSettings.PathIn(_stateDirectory)));
    }

    [Fact]
    public async Task A_machine_with_no_state_folder_is_told_it_is_not_an_installed_agent()
    {
        var missing = Path.Combine(_stateDirectory, "not-installed");

        var exitCode = await new SetUrl(missing, _state, _service, elevated: true)
            .RunAsync(["set-url", "https://adl.example.org"], _output);

        // Pointedly not created here: the MSI locks that folder to SYSTEM and
        // Administrators because the device token is stored in it in the
        // clear, and one made here would inherit whatever ProgramData grants.
        Assert.Equal(1, exitCode);
        Assert.False(Directory.Exists(missing));
        Assert.Contains("not an installed agent", _output.ToString());
    }

    [Fact]
    public async Task A_switch_this_verb_does_not_know_is_a_usage_error_rather_than_a_guess()
    {
        Assert.Equal(2, await Run(["set-url", "https://adl.example.org", "--keep-token"]));

        Assert.Contains("Usage: adl-agent set-url", _output.ToString());
        Assert.False(File.Exists(MachineSettings.PathIn(_stateDirectory)));
    }

    // ---------- who is allowed to run it ----------

    [Fact]
    public async Task Without_administrator_rights_it_says_so_rather_than_failing_on_a_permission()
    {
        Pair();

        var exitCode = await new SetUrl(_stateDirectory, _state, _service, elevated: false)
            .RunAsync(["set-url", "https://adl.example.org"], _output);

        Assert.Equal(1, exitCode);
        Assert.Contains("administrator", _output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(MachineSettings.PathIn(_stateDirectory)));
        Assert.Equal("device-token", _state.Load().Token);
        Assert.Empty(_service.Calls);
    }

    // ---------- and then it finishes the job ----------

    [Fact]
    public async Task The_service_is_stopped_before_the_change_and_started_after_it()
    {
        Assert.Equal(0, await Repoint("https://adl.example.org"));

        // The order, not just the counts. The service rewrites the token on a
        // 401 and the configuration cache on every sync, so a repoint that
        // cleared them under a running service would sometimes find them back
        // a moment later -- a machine paired to an instance it is no longer
        // pointed at, which is the exact state this verb exists to prevent.
        Assert.Equal(["stop", "start"], _service.Calls);
    }

    [Fact]
    public async Task A_service_that_will_not_start_is_reported_rather_than_left_half_applied()
    {
        _service.StartFailsWith = "Access is denied.";

        var exitCode = await Repoint("https://adl.example.org");

        Assert.Equal(1, exitCode);

        // The address was still written -- undoing it would leave the machine
        // pointing at an instance the person standing at it has decided it
        // should not report to. What they are told is the one thing left.
        Assert.Contains("AdlBaseUrl=https://adl.example.org", Settings());
        Assert.Contains("Access is denied.", _output.ToString());
        Assert.Contains("Start the", _output.ToString());
    }

    [Fact]
    public async Task A_service_that_will_not_stop_leaves_the_machine_exactly_as_it_was()
    {
        Pair();
        _service.StopFailsWith = "The service could not be reached.";

        Assert.Equal(1, await Repoint("https://elsewhere.example.org"));

        Assert.False(File.Exists(MachineSettings.PathIn(_stateDirectory)));
        Assert.Equal("device-token", _state.Load().Token);
        Assert.Contains("Nothing was changed.", _output.ToString());
    }

    // ---------- the verb itself ----------

    [Fact]
    public void The_verb_is_told_apart_from_host_configuration_and_from_the_pipe_verbs()
    {
        Assert.True(SetUrl.Handles(["set-url", "https://adl.example.org"]));
        Assert.True(SetUrl.Handles(["set-url"]));
        Assert.False(SetUrl.Handles(["status"]));
        Assert.False(SetUrl.Handles([]));
        Assert.False(SetUrl.Handles(["--Agent:AdlBaseUrl=https://adl.example.org"]));

        // It does not travel over the control surface: redirecting a
        // machine's entire outbound path belongs behind the operating
        // system's consent, not behind a pipe ACL.
        Assert.False(AgentCli.Handles(["set-url", "https://adl.example.org"]));
    }

    // ---------- what a repoint must not touch ----------

    [Fact]
    public async Task A_repoint_leaves_this_machines_logs_where_they_are()
    {
        Pair();

        var logs = Logs();

        Assert.Equal(0, await Repoint("https://elsewhere.example.org"));

        // The pairing, the cache and the sweep log went, because they came
        // from the old instance. The logs did not, because a repoint is very
        // often performed *because* something was wrong, and destroying the
        // evidence at the moment somebody is investigating is the worst
        // available timing.
        Assert.Null(_state.Load().Token);
        Assert.True(File.Exists(logs));
    }

    [Fact]
    public async Task A_repoint_that_keeps_the_pairing_leaves_the_logs_too()
    {
        Pair();

        var logs = Logs();

        Assert.Equal(0, await Repoint("https://elsewhere.example.org", SetUrl.KeepPairingSwitch));

        Assert.Equal("device-token", _state.Load().Token);
        Assert.True(File.Exists(logs));
    }

    /// <summary>A machine with a day of history in it.</summary>
    private string Logs()
    {
        var folder = AgentLogs.In(_stateDirectory);

        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, $"{AgentLogs.CycleLogName}-20260821{CycleLog.Extension}");

        File.WriteAllText(path, "{\"unit\":\"C:\\\\VendorData\\\\Garissa\"}\n");

        return path;
    }

    private Task<int> Repoint(string url, params string[] switches) =>
        Run(["set-url", url, .. switches]);

    private Task<int> Run(string[] args) =>
        new SetUrl(_stateDirectory, _state, _service, elevated: true).RunAsync(args, _output);

    private string Settings() =>
        File.ReadAllText(MachineSettings.PathIn(_stateDirectory));

    private void Seed(string contents) =>
        File.WriteAllText(MachineSettings.PathIn(_stateDirectory), contents);

    /// <summary>A machine that is paired and has synced, as one in the field is.</summary>
    private void Pair()
    {
        _state.Save(new AgentState
        {
            Token = "device-token",
            Device = new DeviceSummary { Id = 7, Name = "Nairobi vendor server" },
            PairedAt = DateTimeOffset.Parse("2026-08-01T09:00:00Z"),
        });

        _state.SaveConfig(new SyncResponse(), DateTimeOffset.Parse("2026-08-21T09:00:00Z"));
        _state.SaveSweeps(new SweepLog
        {
            Swept = new Dictionary<long, DateTimeOffset>
            {
                [7] = DateTimeOffset.Parse("2026-08-21T09:00:00Z"),
            },
        });
    }
}

/// <summary>
/// The service control with the Service Control Manager taken out.
/// </summary>
/// <remarks>
/// It records the order it was called in as well as the counts, because the
/// order is the point: the files this verb changes are files a running
/// service rewrites, so a stop that happened after the write would be a race
/// the machine sometimes loses.
/// </remarks>
internal sealed class RecordingServiceControl : IServiceControl
{
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls => _calls;

    public int Stops => _calls.Count(call => call == "stop");

    public int Starts => _calls.Count(call => call == "start");

    /// <summary>Set to make the start fail with this sentence.</summary>
    public string? StartFailsWith { get; set; }

    /// <summary>Set to make the stop fail with this sentence.</summary>
    public string? StopFailsWith { get; set; }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (StopFailsWith is not null)
        {
            throw new ServiceControlFailedException(StopFailsWith);
        }

        _calls.Add("stop");

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (StartFailsWith is not null)
        {
            throw new ServiceControlFailedException(StartFailsWith);
        }

        _calls.Add("start");

        return Task.CompletedTask;
    }
}
