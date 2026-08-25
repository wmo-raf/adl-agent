using AdlAgent.Core.Update;
using AdlAgent.TestSupport;
using AdlAgent.Tray;

namespace AdlAgent.Core.Tests;

/// <summary>
/// What the tray's window says to do next, and which emptiness an empty
/// station list is.
/// </summary>
/// <remarks>
/// Driven through <see cref="ShellViewModel"/> itself, over the real
/// transport, against a real control service and the fake ADL behind it --
/// the same distance everything else about the local UI is tested at. The
/// window above it is still layout and still not automated; what is under
/// test here is the decision it draws.
/// <para>
/// This is what the view models being moved out of the <c>net10.0-windows</c>
/// tray assembly bought. While they were in it, a <c>net10.0</c> test project
/// could not reference them, and the states below -- which are most of what a
/// technician ever sees -- were covered by reading.
/// </para>
/// </remarks>
public class GuidedWindowTests
{
    private const string Folder = "C:\\VendorData\\Garissa";

    // ---------- the tab the window opens on ----------

    [Fact]
    public async Task An_unpaired_machine_opens_on_the_tab_with_the_one_thing_to_do_on_it()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal(TrayTabs.Pairing, window.SelectedTab);
    }

    [Fact]
    public async Task A_paired_machine_opens_on_its_stations_rather_than_on_a_code_box()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal(TrayTabs.Stations, window.SelectedTab);
    }

    [Fact]
    public async Task A_machine_with_no_ADL_address_opens_on_the_tab_that_says_so()
    {
        await using var agent = Unconfigured();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        // Not Pairing: there is nothing to pair with, and a code box is the
        // one thing on this machine that cannot be the answer.
        Assert.Equal(TrayTabs.Status, window.SelectedTab);
    }

    [Fact]
    public async Task The_poll_does_not_take_a_technician_off_the_tab_they_moved_to()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        window.SelectedTab = TrayTabs.Status;

        // Pairing while the window is open changes what tab the machine's
        // state implies. It must not change what tab the technician is on.
        await agent.PairAsync();
        await window.RefreshAsync();

        Assert.Equal(TrayTabs.Status, window.SelectedTab);
    }

    [Fact]
    public async Task A_window_that_cannot_reach_the_service_picks_no_tab_yet()
    {
        await using var agent = new AgentHarness();

        var window = new ShellViewModel(ServedAgent.NothingServing());

        await window.RefreshAsync();

        // Nothing is known about this machine, so there is no tab that
        // matches it. It stays where it opened, and the line says what is
        // wrong -- rather than the window guessing, and then being unable to
        // correct itself once the service comes up.
        Assert.Equal(TrayTabs.Pairing, window.SelectedTab);

        using var serving = await ServedAgent.ServingAsync(agent);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var reachable = new ShellViewModel(serving.Link);

        await reachable.RefreshAsync();

        Assert.Equal(TrayTabs.Stations, reachable.SelectedTab);
    }

    // ---------- the line, in each state it has to be right for ----------

    [Fact]
    public async Task A_service_that_is_not_running_is_said_to_be_the_thing_that_is_wrong()
    {
        var window = new ShellViewModel(ServedAgent.NothingServing());

        await window.RefreshAsync();

        Assert.Equal(NextStepKind.ServiceNotRunning, window.NextStep.Kind);
        Assert.Equal(TrayState.Stopped, window.NextStep.Attention);
    }

    [Fact]
    public async Task A_machine_with_no_address_is_told_what_to_set_and_who_can_set_it()
    {
        await using var agent = Unconfigured();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Updates.Tier = UpdateTiers.Service;
        agent.HostLifecycle.SettingsFilePath = @"C:\ProgramData\ADL Agent\agent.ini";

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal(NextStepKind.NotConfigured, window.NextStep.Kind);

        // The service's own sentence, carried rather than rewritten: it is
        // the tier that knows whether the answer is a file an administrator
        // edits or an environment variable a technician can set themselves.
        Assert.Contains(@"C:\ProgramData\ADL Agent\agent.ini", window.NextStep.Text);
    }

    [Fact]
    public async Task An_unpaired_machine_is_told_to_paste_the_code_its_administrator_has()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal(NextStepKind.NotPaired, window.NextStep.Kind);
        Assert.Contains("pairing code", window.NextStep.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_revoked_machine_is_told_to_ask_for_another_code()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        agent.Server.TokenRevoked = true;

        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal(NextStepKind.RePairNeeded, window.NextStep.Kind);
    }

    [Fact]
    public async Task A_device_ADL_has_linked_nothing_to_is_told_that_waiting_is_the_right_thing()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal(NextStepKind.NoStationsLinkedYet, window.NextStep.Kind);

        // Who does it, because the technician cannot: linking is an ADL
        // administrator's, in the ADL admin, and nothing on this machine
        // moves it along.
        Assert.Contains("administrator", window.NextStep.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_station_with_no_folder_is_named_and_is_the_thing_to_do()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, Folder, "GARISSA_*.dat"),
            SyncConfigs.Link(12, folder: ""));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal(NextStepKind.BindAFolder, window.NextStep.Kind);
        Assert.Contains("Station 12", window.NextStep.Text);
    }

    [Fact]
    public async Task A_station_that_collected_nothing_is_the_thing_to_do_once_every_folder_is_bound()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        // A folder with the wrong vendor's files in it: the commonest way a
        // bound station still collects nothing, and one the cycle explains.
        agent.Files.Add(Folder, "MOMBASA_20260821.dat", Settled(agent));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();
        await agent.Cycle.RunAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal(NextStepKind.FixAStation, window.NextStep.Kind);
        Assert.Contains("Station 11", window.NextStep.Text);

        // The cycle's own sentence, so the line says which of the two ways a
        // station goes quiet this was.
        Assert.Contains("GARISSA_*.dat", window.NextStep.Text);
    }

    [Fact]
    public async Task A_machine_with_nothing_left_to_do_says_so_and_is_not_amber()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();
        await agent.Cycle.RunAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal(NextStepKind.NothingToDo, window.NextStep.Kind);

        // The acceptance criterion this exists for: the dot in the corner is
        // the line's colour, so it cannot sit amber above a window saying
        // there is nothing to do.
        Assert.Equal(TrayState.Working, window.NextStep.Attention);
    }

    [Fact]
    public async Task A_station_HQ_switched_off_is_not_something_to_do_at_this_machine()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        // No folder, and switched off in ADL. Binding it is not the
        // technician's next step, because it would still be scanned by
        // nothing.
        agent.Server.Config = SyncConfigs.With(
            SyncConfigs.Link(11, Folder, "GARISSA_*.dat"),
            SyncConfigs.Link(12, folder: "", enabled: false));

        agent.Files.Add(Folder, "GARISSA_20260821.dat", Settled(agent));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();
        await agent.Cycle.RunAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal(NextStepKind.NothingToDo, window.NextStep.Kind);
    }

    // ---------- the three ways a station list is empty ----------

    [Fact]
    public async Task An_empty_list_says_ADL_has_linked_nothing_when_that_is_what_happened()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.True(window.HasNoStations);
        Assert.Contains("has not linked any stations", window.NoStationsReason);
    }

    [Fact]
    public async Task An_empty_list_says_ADL_is_not_answering_when_that_is_what_happened()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        agent.Server.Unreachable = true;

        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal(NextStepKind.AdlNotAnswering, window.NextStep.Kind);
        Assert.True(window.HasNoStations);

        // The same empty grid as the test above, and a different problem for
        // a different person. Telling them apart is the whole point.
        Assert.Contains("not answering", window.NoStationsReason);
        Assert.DoesNotContain("has not linked any stations", window.NoStationsReason);
    }

    [Fact]
    public async Task An_empty_list_says_the_service_is_not_running_when_that_is_what_happened()
    {
        var window = new ShellViewModel(ServedAgent.NothingServing());

        await window.RefreshAsync();

        Assert.True(window.HasNoStations);
        Assert.Contains("service is not running", window.NoStationsReason);
        Assert.DoesNotContain("has not linked any stations", window.NoStationsReason);
    }

    // ---------- and it moves on its own ----------

    [Fact]
    public async Task The_line_follows_the_machine_on_the_poll_with_nobody_pressing_anything()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal(NextStepKind.NotPaired, window.NextStep.Kind);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        // The poll, and nothing else: no button, no reopening of the window.
        await window.RefreshAsync();

        Assert.Equal(NextStepKind.NoStationsLinkedYet, window.NextStep.Kind);

        // What an administrator in another building does next, arriving here
        // by itself -- which is what makes a line worth having instead of a
        // wizard that ran once.
        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, folder: ""));

        await agent.Configuration.RefreshAsync();
        await window.RefreshAsync();

        Assert.Equal(NextStepKind.BindAFolder, window.NextStep.Kind);
    }

    [Fact]
    public async Task An_unsaved_folder_path_has_not_bound_anything_and_the_line_says_so()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, folder: ""));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        // Typed into the settings window and not saved. The line is about
        // what ADL holds: nothing has been bound until ADL has taken it, and
        // telling somebody there is nothing to do while their edit is still
        // in a text box is the line being wrong at the one moment it matters.
        var settings = window.BeginEditing(new FolderChoice(_ => null, _ => true))!;

        settings.Station.LocalFolderPath = Folder;

        await window.RefreshAsync();

        Assert.Equal(NextStepKind.BindAFolder, window.NextStep.Kind);
    }

    // ---------- helpers ----------

    private static AgentHarness Unconfigured() =>
        new(settings: new Dictionary<string, string?> { ["Agent:AdlBaseUrl"] = "" });

    private static DateTimeOffset Settled(AgentHarness agent) =>
        agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
}
