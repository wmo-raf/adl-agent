using System.Text;
using AdlAgent.Core;
using AdlAgent.Core.Control;
using AdlAgent.Core.Pairing;
using AdlAgent.Core.Update;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Stories 28, 29 and 30: a fleet that updates itself from its own ADL, an
/// operator who can hold one machine back, and a download nobody trusts
/// until its hash says so.
/// </summary>
/// <remarks>
/// Driven at the same seam as everything else: files, a fake ADL over real
/// HTTP, and the agent doing what it would do. What is faked is the one
/// thing a test cannot survive -- the platform installer, which on a real
/// machine ends this process -- so the assertions stop at the handover: this
/// package, these bytes, this version.
/// <para>
/// The versions are computed from the running assembly rather than written
/// down. This agent's own version is what every one of these decisions is
/// made against, and a test with "0.2.0" in it would start failing on the
/// day the product reached 0.2.0.
/// </para>
/// </remarks>
public class UpdateTests
{
    private static readonly byte[] Package = Encoding.UTF8.GetBytes(
        "not really an MSI, but really these bytes");

    [Fact]
    public async Task A_machine_asks_as_the_tier_it_was_installed_as()
    {
        await using var agent = new AgentHarness();

        agent.Updates.Tier = UpdateTiers.User;

        await agent.PairAsync();
        await agent.Updater.CheckAsync();

        var asked = Assert.Single(agent.Server.RequestsFor("update/"));

        // Which package a machine takes is not a setting anybody types: an
        // install knows how it was installed, and a per-user install has no
        // administrator rights to run the service tier's MSI with.
        Assert.Equal(UpdateTiers.User, asked.Query["tier"]);
    }

    [Fact]
    public async Task A_newer_release_is_fetched_and_handed_to_the_installer()
    {
        await using var agent = new AgentHarness();

        agent.Server.Publish(Newer(), Package);

        await agent.PairAsync();

        var report = await agent.Updater.CheckAsync();

        Assert.Equal(UpdateOutcome.Applying, report.Outcome);

        var applied = Assert.Single(agent.Updates.Applied);

        Assert.Equal(Newer(), applied.Version);
        Assert.Equal(UpdateKinds.Msi, applied.Kind);

        // The bytes on disk are the bytes ADL served -- the thing the
        // installer is about to run, not a description of it.
        Assert.Equal(Package, agent.Updates.BytesOf(applied));
    }

    [Fact]
    public async Task The_release_a_machine_already_runs_is_not_installed_again()
    {
        await using var agent = new AgentHarness();

        agent.Server.Publish(AgentVersion.Current, Package);

        await agent.PairAsync();

        var report = await agent.Updater.CheckAsync();

        Assert.Equal(UpdateOutcome.UpToDate, report.Outcome);
        Assert.Empty(agent.Updates.Applied);
        Assert.Empty(agent.Server.RequestsFor($"update/{AgentVersion.Current}/{UpdateKinds.Msi}/"));
    }

    [Fact]
    public async Task A_package_that_is_not_what_ADL_said_it_is_never_reaches_the_installer()
    {
        await using var agent = new AgentHarness();

        // An instance serving a package whose hash it states wrongly: a
        // corrupted upload, a mirror that fetched half a file, a tampered
        // one. Pilots ship unsigned (decision #262), so this check is the
        // whole of what stands between that and the binary that runs as
        // LocalSystem.
        agent.Server.Publish(Newer(), Package, statedSha256: new string('a', 64));

        await agent.PairAsync();

        var report = await agent.Updater.CheckAsync();

        Assert.Equal(UpdateOutcome.Failed, report.Outcome);
        Assert.Empty(agent.Updates.Applied);

        // And nothing is left behind for anything else to find and run.
        Assert.Empty(Directory.GetFiles(
            Path.Combine(agent.HostLifecycle.StateDirectory, "updates")));
    }

    [Fact]
    public async Task A_package_that_failed_its_hash_is_not_fetched_every_cycle_for_ever()
    {
        await using var agent = new AgentHarness();

        agent.Server.Publish(Newer(), Package, statedSha256: new string('a', 64));

        await agent.PairAsync();

        await agent.Updater.CheckAsync();

        var fetches = agent.Server.RequestsFor($"update/{Newer()}/{UpdateKinds.Msi}/").Count;

        Assert.Equal(1, fetches);

        await agent.Updater.CheckAsync();
        await agent.Updater.CheckAsync();

        // Forty megabytes every ten minutes, down a country link, to fail the
        // same check each time.
        Assert.Equal(fetches, agent.Server.RequestsFor($"update/{Newer()}/{UpdateKinds.Msi}/").Count);
    }

    [Fact]
    public async Task A_pinned_machine_is_never_told_a_newer_release_exists()
    {
        await using var agent = new AgentHarness();

        agent.Server.Publish(AgentVersion.Current, Package);
        agent.Server.Publish(Newer(), Package);
        agent.Server.PinnedVersion = AgentVersion.Current;

        await agent.PairAsync();

        var report = await agent.Updater.CheckAsync();

        Assert.Equal(UpdateOutcome.UpToDate, report.Outcome);
        Assert.Equal(AgentVersion.Current, report.OfferedVersion);
        Assert.True(report.Pinned);
        Assert.Empty(agent.Updates.Applied);
    }

    [Fact]
    public async Task Unpinning_lets_the_machine_move_again()
    {
        await using var agent = new AgentHarness();

        agent.Server.Publish(Newer(), Package);
        agent.Server.PinnedVersion = AgentVersion.Current;

        await agent.PairAsync();
        await agent.Updater.CheckAsync();

        Assert.Empty(agent.Updates.Applied);

        agent.Server.PinnedVersion = null;

        var report = await agent.Updater.CheckAsync();

        Assert.Equal(UpdateOutcome.Applying, report.Outcome);
        Assert.Equal(Newer(), Assert.Single(agent.Updates.Applied).Version);
    }

    [Fact]
    public async Task A_pin_below_the_running_version_holds_the_machine_rather_than_rolling_it_back()
    {
        await using var agent = new AgentHarness();

        agent.Server.Publish(Older(), Package);
        agent.Server.PinnedVersion = Older();

        await agent.PairAsync();

        var report = await agent.Updater.CheckAsync();

        // An agent is never walked backwards by a text field in an admin.
        // Windows Installer would refuse the downgrade anyway; saying so is
        // better than a machine that retries a refused install every cycle.
        Assert.Equal(UpdateOutcome.Held, report.Outcome);
        Assert.True(report.Pinned);
        Assert.Empty(agent.Updates.Applied);
    }

    [Fact]
    public async Task An_instance_holding_no_release_is_not_a_failure()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();

        var report = await agent.Updater.CheckAsync();

        Assert.Equal(UpdateOutcome.NothingOffered, report.Outcome);

        // ADL's own sentence, not one made up here: it knows whether it is
        // holding nothing or holding the wrong thing, and the agent does not.
        Assert.Contains("no published agent release", report.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_release_with_no_package_for_this_tier_is_reported_rather_than_installed()
    {
        await using var agent = new AgentHarness();

        // Published for the per-user tier only -- a release somebody built
        // half of. The service-tier machines must not install the other
        // tier's package, and must not go quiet about why.
        agent.Server.Publish(Newer(), Package, kind: UpdateKinds.VelopackFull);

        await agent.PairAsync();

        var report = await agent.Updater.CheckAsync();

        Assert.Equal(UpdateOutcome.Failed, report.Outcome);
        Assert.Contains(UpdateTiers.Service, report.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(agent.Updates.Applied);
    }

    [Fact]
    public async Task An_install_the_agent_cannot_replace_is_told_rather_than_overwritten()
    {
        await using var agent = new AgentHarness();

        // A folder somebody unzipped, or a developer's `dotnet run`. Running
        // an MSI here would put a second, real install beside the one being
        // worked on.
        agent.Updates.CanApply = false;
        agent.Server.Publish(Newer(), Package);

        await agent.PairAsync();

        var report = await agent.Updater.CheckAsync();

        Assert.Equal(UpdateOutcome.Available, report.Outcome);
        Assert.Equal(Newer(), report.OfferedVersion);
        Assert.Empty(agent.Updates.Applied);
        Assert.Empty(agent.Server.RequestsFor($"update/{Newer()}/{UpdateKinds.Msi}/"));
    }

    [Fact]
    public async Task A_machine_with_automatic_updates_switched_off_reports_the_release_and_leaves_it()
    {
        await using var agent = new AgentHarness(settings: new Dictionary<string, string?>
        {
            ["Agent:AutoUpdate"] = "false",
        });

        agent.Server.Publish(Newer(), Package);

        await agent.PairAsync();

        var report = await agent.Updater.CheckAsync();

        Assert.Equal(UpdateOutcome.Available, report.Outcome);
        Assert.Equal(Newer(), report.OfferedVersion);
        Assert.Empty(agent.Updates.Applied);

        // Not even fetched: a machine that is not going to install it has no
        // reason to pull forty megabytes down a country link.
        Assert.Empty(agent.Server.RequestsFor($"update/{Newer()}/{UpdateKinds.Msi}/"));
    }

    [Fact]
    public async Task An_installer_that_will_not_start_leaves_the_machine_running_and_says_so()
    {
        await using var agent = new AgentHarness();

        agent.Server.Publish(Newer(), Package);
        agent.Updates.FailsWith = "msiexec could not be started.";

        await agent.PairAsync();

        var report = await agent.Updater.CheckAsync();

        Assert.Equal(UpdateOutcome.Failed, report.Outcome);
        Assert.Contains("msiexec", report.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_package_whose_install_did_not_take_is_not_fetched_again()
    {
        await using var agent = new AgentHarness();

        agent.Server.Publish(Newer(), Package);
        agent.Updates.FailsWith = "Windows Installer refused it.";

        await agent.PairAsync();
        await agent.Updater.CheckAsync();

        var fetches = agent.Server.RequestsFor($"update/{Newer()}/{UpdateKinds.Msi}/").Count;

        Assert.Equal(1, fetches);

        // The condition that refused it is usually the machine's, not the
        // package's -- another installation running, a policy, a reboot
        // owed. Retrying is right; paying for the package again every ten
        // minutes while doing so is not.
        await agent.Updater.CheckAsync();

        Assert.Equal(fetches, agent.Server.RequestsFor($"update/{Newer()}/{UpdateKinds.Msi}/").Count);
        Assert.Equal(2, agent.Updates.RefusedCount);

        agent.Updates.FailsWith = null;

        var report = await agent.Updater.CheckAsync();

        // And when it clears, the machine updates from what it already had.
        Assert.Equal(UpdateOutcome.Applying, report.Outcome);
        Assert.Equal(fetches, agent.Server.RequestsFor($"update/{Newer()}/{UpdateKinds.Msi}/").Count);
        Assert.Equal(Package, agent.Updates.BytesOf(Assert.Single(agent.Updates.Applied)));
    }

    [Fact]
    public async Task An_ADL_that_serves_no_update_feed_is_not_an_error()
    {
        await using var agent = new AgentHarness();

        // An instance whose agent plugin predates the feed. The fleet was
        // shipped before the feed existed, and those machines must go on
        // working rather than logging a failure every ten minutes.
        agent.Server.UpdateFeedServed = false;

        await agent.PairAsync();

        var report = await agent.Updater.CheckAsync();

        Assert.Equal(UpdateOutcome.NoFeed, report.Outcome);
    }

    [Fact]
    public async Task An_unreachable_ADL_leaves_the_machine_exactly_where_it_is()
    {
        await using var agent = new AgentHarness();

        agent.Server.Publish(Newer(), Package);

        await agent.PairAsync();

        agent.Server.Unreachable = true;

        var report = await agent.Updater.CheckAsync();

        Assert.Equal(UpdateOutcome.Unreachable, report.Outcome);
        Assert.Empty(agent.Updates.Applied);

        agent.Server.Unreachable = false;

        // And the outage ending is the update happening, with nobody asked
        // to do anything.
        Assert.Equal(UpdateOutcome.Applying, (await agent.Updater.CheckAsync()).Outcome);
    }

    [Fact]
    public async Task A_revoked_machine_stops_asking_about_updates_too()
    {
        await using var agent = new AgentHarness();

        agent.Server.Publish(Newer(), Package);

        await agent.PairAsync();

        agent.Server.TokenRevoked = true;

        var report = await agent.Updater.CheckAsync();

        Assert.Equal(UpdateOutcome.NotPaired, report.Outcome);
        Assert.Equal(PairingState.RePairNeeded, agent.Session.State);
        Assert.Empty(agent.Updates.Applied);

        var callsSoFar = agent.Server.Requests.Count;

        await agent.Updater.CheckAsync();

        Assert.Equal(callsSoFar, agent.Server.Requests.Count);
    }

    [Fact]
    public async Task An_unpaired_machine_asks_nobody()
    {
        await using var agent = new AgentHarness();

        var report = await agent.Updater.CheckAsync();

        Assert.Equal(UpdateOutcome.NotPaired, report.Outcome);
        Assert.Empty(agent.Server.Requests);
    }

    [Fact]
    public async Task The_check_runs_on_the_cycle_cadence()
    {
        await using var agent = new AgentHarness();

        await agent.PairAsync();
        await agent.StartAsync();

        Assert.True(await agent.Server.WaitForRequestsAsync("update/", 1));

        // The scan cycle's cadence, because that is the rhythm the spec sets
        // for noticing things -- and because a second interval to configure
        // is a second interval to get wrong.
        await agent.AdvanceAsync(agent.Cadence.CheckInterval);

        Assert.True(await agent.Server.WaitForRequestsAsync("update/", 2));
    }

    [Fact]
    public async Task What_this_machine_is_doing_about_updates_reaches_the_window()
    {
        await using var agent = new AgentHarness();

        agent.Server.Publish(AgentVersion.Current, Package);
        agent.Server.Publish(Newer(), Package);
        agent.Server.PinnedVersion = AgentVersion.Current;

        await agent.PairAsync();
        await agent.Updater.CheckAsync();

        var status = await agent.ControlService.HandleAsync(
            new ControlRequest(ControlProtocol.StatusCommand));

        Assert.True(status.Ok);

        // The technician standing at a machine HQ says is out of date should
        // be able to see that it is pinned, rather than read service logs to
        // find out whether it is pinned, cut off, or already current.
        Assert.Equal(nameof(UpdateOutcome.UpToDate), status.Data!["update_state"]!.GetValue<string>());
        Assert.Equal(AgentVersion.Current, status.Data["update_version"]!.GetValue<string>());
        Assert.True(status.Data["update_pinned"]!.GetValue<bool>());
    }

    /// <summary>One patch above what this build calls itself.</summary>
    private static string Newer()
    {
        var running = Running();

        return new ReleaseVersion(running.Major, running.Minor, running.Patch + 1).ToString();
    }

    /// <summary>The nearest version below what this build calls itself.</summary>
    private static string Older()
    {
        var running = Running();

        return running switch
        {
            { Patch: > 0 } => new ReleaseVersion(running.Major, running.Minor, running.Patch - 1).ToString(),
            { Minor: > 0 } => new ReleaseVersion(running.Major, running.Minor - 1, 0).ToString(),
            _ => new ReleaseVersion(running.Major - 1, 0, 0).ToString(),
        };
    }

    private static ReleaseVersion Running()
    {
        Assert.True(
            ReleaseVersion.TryParse(AgentVersion.Current, out var running),
            $"This build calls itself '{AgentVersion.Current}', which is not a version the update path can compare.");

        return running;
    }
}
