using AdlAgent.Core.Diagnostics;
using AdlAgent.TestSupport;
using AdlAgent.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Story 8: see and correct any machine's setup without calling someone
/// in-country -- including how much it writes down.
/// </summary>
/// <remarks>
/// The machine's own verbosity setting arrived with wmo-raf/adl#306 and is
/// changed by editing one line of a file on the machine, which requires
/// reaching the machine. That is the exact problem this product exists to
/// solve, so the remaining half is here: ADL may raise it, per device, and
/// clearing the field gives the machine back to whoever is standing at it.
/// </remarks>
public class RemoteVerbosityTests
{
    [Fact]
    public void Nothing_from_ADL_leaves_the_machines_own_setting_standing()
    {
        var verbosity = new LogVerbosity();

        verbosity.SetLocal("Warning");

        Assert.False(verbosity.Adopt(null));
        Assert.False(verbosity.Overridden);
        Assert.Equal(LogLevel.Warning, verbosity.Effective);

        // Empty is the same silence a cleared admin field sends, and a word
        // nobody can parse is a typo -- neither is an instruction.
        Assert.False(verbosity.Adopt(""));
        Assert.False(verbosity.Adopt("chatty"));
        Assert.Equal(LogLevel.Warning, verbosity.Effective);
    }

    [Fact]
    public void ADL_outranks_the_machine_and_giving_it_back_is_one_cleared_field()
    {
        var verbosity = new LogVerbosity();

        verbosity.SetLocal("Information");

        Assert.True(verbosity.Adopt("debug"));
        Assert.True(verbosity.Overridden);
        Assert.Equal(LogLevel.Debug, verbosity.Effective);

        // Said again, and nothing moved: the pipeline is not rebuilt every
        // cycle for a setting that has not changed.
        Assert.False(verbosity.Adopt("Debug"));

        Assert.True(verbosity.Adopt(null));
        Assert.False(verbosity.Overridden);
        Assert.Equal(LogLevel.Information, verbosity.Effective);
    }

    /// <summary>
    /// <c>None</c> parses, and is refused with the nonsense.
    /// </summary>
    /// <remarks>
    /// It means "log nothing at all", set from a form on another continent --
    /// a machine that has silently stopped keeping the only evidence anybody
    /// will have of its next bad day. There is no support case that wants it,
    /// and the log's own ceiling already makes a chatty machine harmless.
    /// </remarks>
    [Fact]
    public void ADL_cannot_switch_a_machines_log_off()
    {
        var verbosity = new LogVerbosity();

        verbosity.SetLocal("Information");

        Assert.False(verbosity.Adopt("None"));
        Assert.Equal(LogLevel.Information, verbosity.Effective);
    }

    /// <summary>
    /// The half that a unit test of <see cref="LogVerbosity"/> cannot reach:
    /// the logging pipeline is built once at start-up, so a level that moves
    /// afterwards has to reach the loggers already handed out.
    /// </summary>
    /// <remarks>
    /// Without this a machine HQ had put on <c>Debug</c> would go on writing
    /// <c>Information</c> until somebody restarted the service -- which is
    /// the one thing a remote verbosity must not need.
    /// </remarks>
    [Fact]
    public void Raising_the_level_reaches_the_loggers_already_running()
    {
        var state = Directory.CreateTempSubdirectory("adl-agent-verbosity").FullName;

        using var host = WindowsAgentHost.CreateBuilder([
            "--Agent:AdlBaseUrl=https://adl.example.org",
            $"--Agent:StateDirectory={state}",
            "--Agent:LogLevel=Warning",
        ]).Build();

        var logger = host.Services.GetRequiredService<ILogger<RemoteVerbosityTests>>();

        Assert.False(logger.IsEnabled(LogLevel.Debug));

        var verbosity = host.Services.GetRequiredService<LogVerbosity>();

        Assert.True(verbosity.Adopt("Debug"));
        Assert.True(logger.IsEnabled(LogLevel.Debug));

        // And back, on the same running logger.
        Assert.True(verbosity.Adopt(null));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
    }

    [Fact]
    public async Task A_sync_carries_ADLs_word_on_how_much_this_machine_logs()
    {
        await using var agent = new AgentHarness();

        var sample = FakeAdlServer.SampleConfig();

        agent.Server.Config = sample with
        {
            Device = sample.Device with { LogLevel = "Debug" },
        };

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        Assert.Equal(LogLevel.Debug, Verbosity(agent).Effective);

        agent.Server.Config = sample with
        {
            Device = sample.Device with { LogLevel = null },
        };

        await agent.Configuration.RefreshAsync();

        Assert.Equal(LogLevel.Information, Verbosity(agent).Effective);
    }

    /// <summary>
    /// It survives a restart on a machine that cannot reach ADL, because it
    /// comes back out of the offline cache.
    /// </summary>
    /// <remarks>
    /// A machine HQ has put on Debug is very often a machine whose link is
    /// the thing being investigated. A verbosity that only lasted while ADL
    /// was reachable would be off again by the time anybody read the log.
    /// </remarks>
    [Fact]
    public async Task A_raised_level_survives_a_restart_with_no_link()
    {
        var store = new InMemoryAgentStateStore();

        await using (var agent = new AgentHarness(store))
        {
            var sample = FakeAdlServer.SampleConfig();

            agent.Server.Config = sample with
            {
                Device = sample.Device with { LogLevel = "Debug" },
            };

            await agent.PairAsync();
            await agent.Configuration.RefreshAsync();
        }

        await using var restarted = new AgentHarness(store);

        restarted.Server.Unreachable = true;

        var working = await restarted.Configuration.RefreshAsync();

        Assert.NotNull(working);
        Assert.True(working!.FromCache);
        Assert.Equal(LogLevel.Debug, Verbosity(restarted).Effective);
    }

    private static LogVerbosity Verbosity(AgentHarness agent) =>
        agent.Services.GetRequiredService<LogVerbosity>();

    /// <summary>
    /// A machine whose settings file says nothing about logging is one ADL
    /// can still raise.
    /// </summary>
    /// <remarks>
    /// The rule that used to exist here was "add a filter only when the
    /// machine asked", so that a developer's appsettings.json was not
    /// overruled by a default nobody typed. That still holds -- but ADL
    /// saying so is somebody typing it.
    /// </remarks>
    [Fact]
    public void A_machine_that_never_set_a_level_can_still_be_raised_from_ADL()
    {
        var state = Directory.CreateTempSubdirectory("adl-agent-verbosity").FullName;

        using var host = WindowsAgentHost.CreateBuilder([
            "--Agent:AdlBaseUrl=https://adl.example.org",
            $"--Agent:StateDirectory={state}",
        ]).Build();

        var logger = host.Services.GetRequiredService<ILogger<RemoteVerbosityTests>>();
        var verbosity = host.Services.GetRequiredService<LogVerbosity>();

        Assert.True(verbosity.Adopt("Warning"));
        Assert.False(logger.IsEnabled(LogLevel.Information));
    }
}
