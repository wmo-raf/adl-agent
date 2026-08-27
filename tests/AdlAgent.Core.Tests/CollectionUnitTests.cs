using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Collection happens a unit at a time, and each unit finishes on its own.
/// </summary>
/// <remarks>
/// A unit is a station and whatever it shares a folder with -- one station
/// and one folder, for almost every station in a fleet. It is neither the
/// station nor the folder, because neither survives the two ways they fail to
/// line up: several stations write into one dump directory, which has to be
/// walked once between them, and one station filed by date is spread over as
/// many dated directories as its window holds.
/// <para>
/// What that buys is in wmo-raf/adl#304. A machine working through a first
/// bind's backlog used to go hours without finishing anything ADL could see,
/// because the one mark it had was stamped only after a pass over every
/// station on the box. Now each unit stamps its own, and a station's reasons
/// for being silent reach an operator without waiting on a folder it has
/// nothing to do with.
/// </para>
/// </remarks>
public class CollectionUnitTests
{
    private const string Garissa = "C:\\VendorData\\Garissa";
    private const string Kisumu = "C:\\VendorData\\Kisumu";
    private const string Shared = "C:\\VendorData\\All";

    [Fact]
    public async Task Stations_sharing_a_folder_are_collected_as_one_unit()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, Shared, "GARISSA_*.dat"),
            SyncConfigs.Link(12, Shared, "MOMBASA_*.dat"));

        agent.Files
            .Add(Shared, "GARISSA_20260821.dat", Settled(agent), "g\n")
            .Add(Shared, "MOMBASA_20260821.dat", Settled(agent), "m\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // The point of grouping by what stations share rather than by the
        // station: two stations in one dump directory still cost one walk.
        Assert.Equal(1, agent.Files.EnumerationsOf(Shared));

        Assert.Equal(2, agent.Cycles.LastCompletedCycle!.Links.Count);
    }

    [Fact]
    public async Task A_station_with_a_folder_of_its_own_is_a_unit_of_its_own()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, Garissa, "*.dat"),
            SyncConfigs.Link(12, Kisumu, "*.dat"));

        agent.Files
            .Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "g\n")
            .Add(Kisumu, "KISUMU_20260821.dat", Settled(agent), "k\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal(1, agent.Files.EnumerationsOf(Garissa));
        Assert.Equal(1, agent.Files.EnumerationsOf(Kisumu));

        // Two units, and both of them reported. The grid does not care how
        // the machine cut its work up; every station has to be in it.
        Assert.Equal(2, agent.Cycles.LastCompletedCycle!.Links.Count);
    }

    [Fact]
    public async Task A_unit_cut_short_still_says_what_it_scanned_and_why_it_stopped()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // A file appears, and then the country's uplink goes. The sync is
        // served from the cache the last one left, so the tick starts and
        // dies at the manifest -- which is where a cycle dies in the field.
        agent.Files.Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n");
        agent.Server.Unreachable = true;

        await agent.Cycle.RunAsync();

        // Before this, the whole pass recorded nothing when it was cut short,
        // and the station showed "no cycle yet" -- with no way for anyone to
        // tell that from a station nobody had ever collected.
        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Equal(11, link.StationLinkId);
        Assert.Equal(1, link.Scanned);
    }

    [Fact]
    public async Task A_unit_cut_short_does_not_move_the_completion_mark()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var completedAt = agent.Cycles.LastCompletedCycle!.CompletedAt;

        agent.Time.Advance(TimeSpan.FromMinutes(10));
        agent.Files.Add(Garissa, "GARISSA_20260821.dat", Settled(agent), "today\n");
        agent.Server.Unreachable = true;

        await agent.Cycle.RunAsync();

        // The counts of a pass that died are worth having; its completion is
        // not. A machine whose every pass is cut short is exactly the machine
        // ADL is meant to call stuck, and a mark moved here would spend that
        // alarm on the one fleet that needs it.
        Assert.Equal(completedAt, agent.Cycles.LastCompletedCycle!.CompletedAt);
    }

    [Fact]
    public async Task A_unit_that_finished_keeps_its_completion_when_a_later_one_dies()
    {
        await using var agent = new AgentHarness();

        // Garissa has nothing to offer, so its unit finishes without needing
        // ADL at all. Kisumu has a file, and its unit is the one that dies.
        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, Garissa, "*.dat"),
            SyncConfigs.Link(12, Kisumu, "*.dat"));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        agent.Time.Advance(TimeSpan.FromMinutes(10));
        agent.Files.Add(Kisumu, "KISUMU_20260821.dat", Settled(agent), "today\n");
        agent.Server.Unreachable = true;

        var at = agent.Time.GetUtcNow();

        await agent.Cycle.RunAsync();

        // This is the whole of the change. One unit finishing is a fact about
        // this machine, and it survives another unit failing -- where before,
        // one folder that could not be delivered threw away what every other
        // folder on the box had just done.
        Assert.Equal(at, agent.Cycles.LastCompletedCycle!.CompletedAt);
    }

    [Fact]
    public async Task A_machine_with_nothing_to_collect_still_finishes_a_pass()
    {
        await using var agent = new AgentHarness();

        // Every station switched off in ADL, so the machine plans no units at
        // all. It is not broken and it is not idle-by-fault: it goes round an
        // empty fleet every check interval, exactly as it is told to.
        agent.Server.Config = SyncConfigs.With(
            connectionEnabled: false,
            SyncConfigs.Link(11, Garissa, "*.dat"));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // Reported as having finished, because it has. Saying otherwise would
        // have ADL call a switched-off country a country whose machines have
        // stopped.
        Assert.NotNull(agent.Cycles.LastCompletedCycle);
        Assert.Empty(agent.Cycles.LastCompletedCycle!.Links);
    }

    [Fact]
    public async Task A_station_moved_off_this_machine_stops_being_reported()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, Garissa, "*.dat"),
            SyncConfigs.Link(12, Kisumu, "*.dat"));

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal(2, agent.Cycles.LastCompletedCycle!.Links.Count);

        // HQ moves Kisumu to another machine.
        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Garissa, "*.dat"));

        await agent.Cycle.RunAsync();

        // Each unit overwrites its own stations and leaves the rest alone, so
        // without pruning a station would go on reporting its last counts and
        // its backlog to ADL for the life of the service.
        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Equal(11, link.StationLinkId);
    }

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
}
