using AdlAgent.TestSupport;
using AdlAgent.Tray;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The settings window, without the window: what opening one does to the list
/// behind it, what a save comes to, and what Cancel costs.
/// </summary>
/// <remarks>
/// Driven through <see cref="ShellViewModel"/> and
/// <see cref="StationSettingsViewModel"/> over the real transport, against a
/// real control service and the fake ADL behind it -- the same distance the
/// rest of the local UI is tested at.
/// <para>
/// What is left unautomated above this line is layout and three lines of
/// dialog. The rule the window follows -- close on <see cref="SaveOutcome"/>
/// unless it is a refusal -- is one switch over what these tests assert.
/// </para>
/// </remarks>
public class StationSettingsTests
{
    private const string Folder = "C:\\VendorData\\Garissa";

    // ---------- what is typed into is a copy ----------

    [Fact]
    public async Task The_row_goes_on_showing_what_ADL_holds_while_the_window_is_typed_into()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var row = window.SelectedStation!;
        var settings = window.BeginEditing(Anywhere())!;

        settings.Station.LocalFolderPath = "C:\\Somewhere\\Else";

        // Nothing has been bound until ADL has taken it, and the list is
        // about what ADL holds. This is also what makes Cancel free: there is
        // no revert to run, because the row was never changed.
        Assert.Equal(Folder, row.LocalFolderPath);
        Assert.False(row.HasChanges);
        Assert.True(settings.Station.HasChanges);
    }

    [Fact]
    public async Task Closing_without_saving_leaves_ADL_and_the_row_exactly_as_they_were()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var settings = window.BeginEditing(Anywhere())!;

        settings.Station.LocalFolderPath = "C:\\Somewhere\\Else";
        settings.Station.FilePattern = "*.csv";

        // Cancel: the window closes, the copy is dropped, and this is the
        // refresh that follows it.
        settings.Done();

        await window.RefreshAsync();

        Assert.Equal(Folder, window.SelectedStation!.LocalFolderPath);
        Assert.Equal("GARISSA_*.dat", window.SelectedStation.FilePattern);
    }

    // ---------- the three things a save comes to ----------

    [Fact]
    public async Task A_folder_ADL_takes_is_saved_and_the_window_is_finished()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, folder: ""));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var settings = window.BeginEditing(Anywhere())!;

        settings.Station.LocalFolderPath = Folder;
        settings.Station.FilePattern = "GARISSA_*.dat";

        Assert.Equal(SaveOutcome.Saved, await settings.SaveAsync());

        // The window closes on this, so the sentence is read behind it.
        Assert.Contains("Saved to ADL", window.Message, StringComparison.Ordinal);

        settings.Done();

        await window.RefreshAsync();

        // And the list says the same thing, because it came back from ADL
        // rather than being patched in place here.
        Assert.Equal(Folder, window.SelectedStation!.LocalFolderPath);
    }

    [Fact]
    public async Task A_refusal_keeps_the_window_open_on_the_thing_that_has_to_change()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var settings = window.BeginEditing(Anywhere())!;

        // The refusal a technician is likeliest to meet: they cleared the box
        // to retype it and pressed Save before they had.
        settings.Station.LocalFolderPath = "";

        Assert.Equal(SaveOutcome.Refused, await settings.SaveAsync());

        // Read in front of the window rather than behind it, because the
        // window does not close on this one: what it is showing is the thing
        // that has to change.
        Assert.Contains("folder", settings.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(window.Message, settings.Answer);
    }

    [Fact]
    public async Task A_revoked_machine_ends_the_window_and_leaves_the_line_behind_it_saying_why()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        agent.Server.TokenRevoked = true;

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var settings = window.BeginEditing(Anywhere())!;

        settings.Station.FilePattern = "*.csv";

        // Not a refusal to fix in this window: nothing typed into it can be
        // saved by anybody until the machine is paired again, and a window
        // left open would invite a technician to retry into a wall.
        Assert.Equal(SaveOutcome.MustRePair, await settings.SaveAsync());

        settings.Done();

        Assert.Equal(NextStepKind.RePairNeeded, window.NextStep.Kind);
    }

    // ---------- and what the line along the bottom says ----------

    [Fact]
    public async Task An_untouched_window_says_why_its_Save_button_is_grey()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var settings = window.BeginEditing(Anywhere())!;

        // A disabled button with no stated reason is a button somebody
        // stares at wondering when it turns on.
        Assert.False(settings.SaveCommand.CanExecute(null));
        Assert.Equal("Nothing has changed yet.", settings.Answer);

        settings.Station.FilePattern = "*.csv";

        Assert.True(settings.SaveCommand.CanExecute(null));
        Assert.Equal("", settings.Answer);
    }

    [Fact]
    public async Task Typing_again_clears_what_ADL_said_about_the_boxes_as_they_were()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var settings = window.BeginEditing(Anywhere())!;

        settings.Station.LocalFolderPath = "";

        await settings.SaveAsync();

        Assert.Contains("folder", settings.Answer, StringComparison.OrdinalIgnoreCase);

        // The complaint was about the boxes as they were, and they are not
        // those any more. Left standing, it would sit there contradicting the
        // box above it while somebody fixed exactly what it asked for.
        settings.Station.LocalFolderPath = "C:\\VendorData\\Mombasa";

        Assert.Equal("", settings.Answer);

        // And typing the folder ADL already holds back into the box is not a
        // third state: there is nothing to send, so the line goes back to
        // saying why the button beside it is grey.
        settings.Station.LocalFolderPath = Folder;

        Assert.Equal("Nothing has changed yet.", settings.Answer);
    }

    // ---------- what the poll may do while a window is open ----------

    [Fact]
    public async Task The_poll_does_not_replace_the_station_a_window_is_open_on()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var row = window.SelectedStation!;

        // Opened, and nothing typed into it yet. This is the gap the previous
        // rule -- "do not rebuild while somebody is typing" -- was blind to:
        // there is nothing typed, so it would have rebuilt, and the window
        // would have been left editing a station the list no longer held.
        window.BeginEditing(Anywhere());

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, "C:\\Moved", "*.dat"));

        await agent.Configuration.RefreshAsync();
        await window.RefreshAsync();

        Assert.Same(row, window.SelectedStation);
        Assert.Equal(Folder, window.SelectedStation!.LocalFolderPath);
    }

    [Fact]
    public async Task The_line_and_the_tray_icon_go_on_moving_while_a_window_is_open()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        window.BeginEditing(Anywhere());

        agent.Server.TokenRevoked = true;

        await agent.Configuration.RefreshAsync();
        await window.RefreshAsync();

        // Only the rows are frozen. A technician who leaves a settings window
        // open should not be sitting above a tray icon that has quietly
        // stopped telling the truth.
        Assert.Equal(NextStepKind.RePairNeeded, window.NextStep.Kind);
    }

    [Fact]
    public async Task The_rows_catch_up_once_the_window_has_closed()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var settings = window.BeginEditing(Anywhere())!;

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, "C:\\Moved", "*.dat"));

        await agent.Configuration.RefreshAsync();
        await window.RefreshAsync();

        Assert.Equal(Folder, window.SelectedStation!.LocalFolderPath);

        settings.Done();

        await window.RefreshAsync();

        Assert.Equal("C:\\Moved", window.SelectedStation!.LocalFolderPath);
    }

    [Fact]
    public async Task Nothing_selected_is_nothing_to_open()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With();

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.True(window.HasNoStations);
        Assert.Null(window.BeginEditing(Anywhere()));
    }

    // ---------- the count, before anything is typed ----------

    [Fact]
    public async Task A_window_opens_saying_what_the_folder_ADL_holds_is_finding()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "GARISSA_*.dat"));

        agent.Files.Add(Folder, "GARISSA_20260821.dat", agent.Time.GetUtcNow());
        agent.Files.Add(Folder, "GARISSA_20260822.dat", agent.Time.GetUtcNow());

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var settings = window.BeginEditing(Anywhere())!;

        // The window counts as it opens rather than waiting for a keystroke:
        // whether the folder ADL already holds is finding anything is
        // frequently the whole reason somebody opened it, and an empty panel
        // where the answer goes is not an answer.
        await settings.CountAsync();

        Assert.Contains("2 files match", settings.Station.MatchSummary, StringComparison.Ordinal);
    }

    /// <summary>A machine where nothing is mapped and every folder is there.</summary>
    private static FolderChoice Anywhere() => new(_ => null, _ => true);
}
