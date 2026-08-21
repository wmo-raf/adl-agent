using AdlAgent.Core.Api;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// What happens when ADL will not take the page it was offered.
/// </summary>
/// <remarks>
/// "Retry is the next cycle re-asking" is the whole of the agent's error
/// handling, and it rests on the next cycle being different in some way. A
/// page ADL refuses for a reason that will still be true next cycle breaks
/// that: the scan is deterministic, so the identical page is rebuilt, refused
/// again, and a station goes quiet for ever with nothing but a repeating
/// message to show for it.
/// <para>
/// No refusal an agent of this version can actually earn is known -- every
/// rule ADL applies to an entry is one this agent already satisfies by
/// construction. That is exactly why these are worth having: the failure
/// would arrive with a future server-side rule, or a bug in a later agent,
/// and it would arrive as a folder that silently stopped delivering rather
/// than as anything anyone could read. The agent's job is to make an
/// unanticipated refusal cost the file it is about, not the four hundred and
/// ninety-nine files next to it.
/// </para>
/// </remarks>
public class RefusedManifestTests
{
    private const string Folder = "C:\\VendorData\\Garissa";

    [Fact]
    public async Task One_file_ADL_cannot_read_does_not_take_the_page_with_it()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));

        // One file this instance will not read as an entry -- for a reason
        // the agent does not know and cannot anticipate. It sits beside
        // twenty ordinary files, and ADL refuses the whole manifest for it.
        var unreadable = new string('x', 200) + ".dat";

        agent.Server.UnreadableNames.Add(unreadable);
        agent.Files.Add(Folder, unreadable, Settled(agent), "unreadable\n");

        for (var index = 0; index < 20; index++)
        {
            agent.Files.Add(Folder, $"GARISSA_{index:00}.dat", Settled(agent), $"row {index}\n");
        }

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        // The twenty blameless files got through on the retried page.
        Assert.Equal(20, agent.Server.Ledger.Count);
        Assert.Null(agent.Server.Held(11, unreadable));

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Equal(20, link.Uploaded);
        Assert.Equal(1, link.Failed);

        // And the technician is told which file, in ADL's own words -- the
        // only place anyone will ever see why one file of twenty-one is not
        // arriving.
        Assert.Contains(unreadable, link.Error!);
        Assert.Contains("could not be read as a manifest entry", link.Error!);
    }

    [Fact]
    public async Task A_page_ADL_can_read_none_of_is_charged_once_and_not_twice()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));

        // Every file in the folder refused. There is no smaller page to fall
        // back to, so the whole page is given up on -- once. A station told
        // it lost six files when it had three is a station whose numbers
        // nobody can use.
        for (var index = 0; index < 3; index++)
        {
            var name = $"GARISSA_{index:00}.dat";

            agent.Server.UnreadableNames.Add(name);
            agent.Files.Add(Folder, name, Settled(agent), $"row {index}\n");
        }

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Equal(3, link.Scanned);
        Assert.Equal(3, link.Failed);
        Assert.Equal(0, link.Uploaded);
        Assert.Empty(agent.Server.Ledger);
    }

    [Fact]
    public async Task An_instance_that_takes_fewer_files_than_it_said_is_followed_down()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat")) with
        {
            Limits = new AgentLimits { ManifestEntries = 500, FileBytes = 50 * 1024 * 1024 },
        };

        // The number in the sync response is not the number this instance
        // will actually take. Believing it would earn a refusal on every page
        // of every cycle, for ever.
        agent.Server.ManifestEntriesActuallyAccepted = 4;

        for (var index = 0; index < 10; index++)
        {
            agent.Files.Add(Folder, $"GARISSA_{index:00}.dat", Settled(agent), $"row {index}\n");
        }

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        Assert.Equal(10, agent.Server.Ledger.Count);
        Assert.All(
            agent.Server.ManifestPages.Where(page => page.Count <= 4),
            page => Assert.True(page.Count <= 4));

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        Assert.Equal(10, link.Uploaded);
        Assert.Equal(0, link.Failed);
    }

    [Fact]
    public async Task A_page_refused_for_a_reason_ADL_will_not_name_is_reported_and_left()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat")) with
        {
            // Zero is not a page size. The agent clamps it to one, offers one
            // file at a time, and the instance still refuses every page --
            // there is no smaller page to fall back to.
            Limits = new AgentLimits { ManifestEntries = 0, FileBytes = 50 * 1024 * 1024 },
        };

        agent.Server.ManifestEntriesActuallyAccepted = 0;

        agent.Files.Add(Folder, "GARISSA_00.dat", Settled(agent), "row\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var link = Assert.Single(agent.Cycles.LastCompletedCycle!.Links);

        // Reported rather than looped on: the cycle still finishes, and what
        // it could not do arrives at HQ as a sentence.
        Assert.Equal(1, link.Failed);
        Assert.Equal(0, link.Uploaded);
        Assert.NotNull(link.Error);
        Assert.NotNull(agent.Cycles.LastCompletedCycle!.CompletedAt);
    }

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
}
