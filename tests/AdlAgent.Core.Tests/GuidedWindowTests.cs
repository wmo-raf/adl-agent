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

        Assert.Equal(TrayTabs.Status, window.SelectedTab);
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

        // Not Stations: there is nothing to pair with and nothing linked, and
        // the code box this tab now carries is the one thing on this machine
        // that cannot be the answer.
        Assert.Equal(TrayTabs.Status, window.SelectedTab);
    }

    // ---------- the tab a machine is not allowed on yet ----------

    /// <summary>
    /// Every state, and whether the station list can be opened in it.
    /// </summary>
    /// <remarks>
    /// Exhaustive on purpose. The rule itself ends in a catch-all, so a state
    /// added later is available until somebody decides otherwise -- which is
    /// the right default and a silent one. This is what stops it being silent:
    /// a new <see cref="NextStepKind"/> fails here until it is written down.
    /// </remarks>
    [Fact]
    public void Every_state_says_whether_the_station_list_can_be_opened()
    {
        // The two a machine has never paired in. Behind the tab in both is one
        // sentence saying so, and a tab that looks like a working one is a tab
        // somebody clicks during the five minutes when the only thing to do is
        // on the other one.
        var shut = new[] { NextStepKind.NotConfigured, NextStepKind.NotPaired };

        foreach (var kind in Enum.GetValues<NextStepKind>())
        {
            Assert.Equal(!shut.Contains(kind), TrayTabs.Available(Step(kind)));
        }
    }

    /// <summary>
    /// The state that looks like it belongs with those two and must not.
    /// </summary>
    /// <remarks>
    /// A revoked machine has been collecting for months and its station list
    /// is still on disk. Somebody is looking at it because it broke, and the
    /// list is what they came to read.
    /// </remarks>
    [Fact]
    public void A_machine_whose_token_was_revoked_keeps_its_station_list()
    {
        Assert.True(TrayTabs.Available(Step(NextStepKind.RePairNeeded)));
    }

    /// <summary>
    /// A step of one kind, carrying nothing else.
    /// </summary>
    /// <remarks>
    /// The rule under test reads the kind and nothing else, and the words a
    /// real step carries are what the states themselves are tested through
    /// elsewhere in this file.
    /// </remarks>
    private static NextStep Step(NextStepKind kind) =>
        new() { Kind = kind, Text = string.Empty, Attention = TrayState.Unknown };

    [Fact]
    public async Task An_unpaired_machine_cannot_open_the_station_list()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.False(window.StationsAvailable);
    }

    [Fact]
    public async Task A_redeemed_code_is_the_whole_bar_for_the_station_list()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.False(window.StationsAvailable);

        // Paired, and ADL has not answered yet: nothing is in the list and the
        // tab opens anyway. Anything stricter would tie a tab to the network,
        // and a machine that pairs correctly behind a firewall rule nobody has
        // written yet would stay shut for ever.
        await agent.PairAsync();
        await window.RefreshAsync();

        Assert.True(window.StationsAvailable);
    }

    /// <summary>
    /// It comes alive where the technician is, rather than moving them.
    /// </summary>
    /// <remarks>
    /// Pairing succeeds on the Status tab, which is where the code box is and
    /// where the result of pressing it is read. The tab lighting up is the
    /// feedback; jumping to it would take somebody who has just been told
    /// pairing worked to a second sentence saying ADL has not answered yet.
    /// </remarks>
    [Fact]
    public async Task Pairing_opens_the_station_list_without_moving_anybody_to_it()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal(TrayTabs.Status, window.SelectedTab);

        await agent.PairAsync();
        await window.RefreshAsync();

        Assert.True(window.StationsAvailable);
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

        var opened = window.SelectedTab;

        await window.RefreshAsync();

        // Nothing is known about this machine, so there is no tab that
        // matches it. It stays where it opened, and the line says what is
        // wrong -- rather than the window guessing, and then being unable to
        // correct itself once the service comes up.
        //
        // Against where it opened rather than against a named tab: the
        // invariant is that nothing was chosen, and a constant here would go
        // on passing while claiming the window had chosen that one.
        Assert.Equal(opened, window.SelectedTab);

        using var serving = await ServedAgent.ServingAsync(agent);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var reachable = new ShellViewModel(serving.Link);

        await reachable.RefreshAsync();

        Assert.Equal(TrayTabs.Stations, reachable.SelectedTab);
    }

    // ---------- the pairing row, and what it offers ----------

    [Fact]
    public async Task An_unpaired_machine_shows_a_code_box()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal("Not paired yet", window.PairingLine);
        Assert.True(window.ShowsPairingBox);

        // Nothing to offer a way back to: the way is already open.
        Assert.False(window.ShowsPairAgain);
    }

    [Fact]
    public async Task A_machine_that_has_never_paired_shows_no_ADL_facts()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        // ADL has told this machine nothing, so the rows ADL fills are not
        // drawn at all. They were six dashes and a blank row around the one
        // thing there was to do.
        Assert.False(window.HasEverPaired);
        Assert.Equal("", window.PairedSince);

        // Including in the strip above every tab, which would otherwise go on
        // announcing a scan interval this machine is not keeping.
        Assert.False(window.ShowsAdlFacts);

        // The version stands alone there, because it is this machine's own
        // fact rather than ADL's, and it is the first thing HQ asks for down
        // a telephone -- including about a machine that has never paired.
        Assert.NotEqual("-", window.AgentVersion);

        // And the header's first line is the sentence saying so, not an
        // address: there is nothing paired to link to, and the dot that would
        // carry ADL's verdict has no verdict to carry.
        Assert.True(window.ShowsHeadline);
        Assert.False(window.ShowsPairedTo);
        Assert.NotEqual("", window.Headline);
    }

    [Fact]
    public async Task A_paired_machine_shows_no_code_box_but_can_ask_for_one()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal("Paired", window.PairingLine);
        Assert.True(window.HasEverPaired);
        Assert.StartsWith("since ", window.PairedSince);

        // The whole point of folding the tab in: a machine that paired months
        // ago is not shown a box it must not use.
        Assert.False(window.ShowsPairingBox);
        Assert.True(window.ShowsPairAgain);
    }

    [Fact]
    public async Task A_revoked_machine_shows_a_code_box_again()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        agent.Server.TokenRevoked = true;

        await agent.HeartbeatLoop.BeatAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.Equal("Revoked by ADL", window.PairingLine);
        Assert.True(window.ShowsPairingBox);
        Assert.False(window.ShowsPairAgain);

        // "since" would say the machine is still paired. The moment is when
        // what is on screen stopped being true, not when it started.
        Assert.StartsWith("paired ", window.PairedSince);
    }

    [Fact]
    public async Task A_revoked_machine_keeps_the_facts_ADL_gave_it()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();
        await agent.HeartbeatLoop.BeatAsync();

        agent.Server.TokenRevoked = true;

        await agent.HeartbeatLoop.BeatAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        // The regression this test exists for. Gating ADL's rows on "is
        // paired" reads correctly on a machine that has just been installed,
        // and takes the last heartbeat, the last sync and the last problem
        // off the screen of a machine that has just been cut off -- which is
        // the one moment in its life somebody needs to read them.
        Assert.True(window.HasEverPaired);
        Assert.NotEqual("-", window.LastHeartbeat);
        Assert.True(window.ShowsAdlFacts);

        // But not the verdict beside them. ADL's last word about this machine
        // is from before it cut the machine off, and a green dot reading
        // "Collecting and sending" under a headline saying nothing is being
        // sent is a contradiction the header must not be able to draw.
        Assert.False(window.ShowsPairedTo);

        // So the headline is the sentence about the revocation, and it is the
        // one line in the header with any weight.
        Assert.True(window.ShowsHeadline);
        Assert.Contains("revoked", window.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pair_again_opens_the_box_on_a_healthy_machine()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        // ADL rotates a code without revoking the token it replaces, on
        // purpose, so that a machine still shipping data does not stop
        // between an administrator's click and a technician typing the code
        // in. Nothing about this machine will ever ask for a box, and
        // somebody is standing at it holding a code.
        window.PairAgain();

        Assert.True(window.ShowsPairingBox);
        Assert.False(window.ShowsPairAgain);

        // And the poll does not close it again under whoever is typing.
        await window.RefreshAsync();

        Assert.True(window.ShowsPairingBox);
    }

    [Fact]
    public async Task A_box_opened_by_mistake_can_be_put_away_again()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        window.PairAgain();
        window.PairingCode = "KX7M-93";

        // Only here. On a machine that has never paired, or one ADL has
        // revoked, the box is the page and there is nothing to cancel to.
        Assert.True(window.ShowsCancelPairing);

        window.CancelPairAgain();

        Assert.False(window.ShowsPairingBox);
        Assert.True(window.ShowsPairAgain);

        // The half-typed credential goes with it, rather than waiting in the
        // box for whoever opens this window next.
        Assert.Equal("", window.PairingCode);

        // The window hides rather than closes, so this has to survive the
        // poll -- a box that reopened five seconds later would be no cancel
        // at all.
        await window.RefreshAsync();

        Assert.False(window.ShowsPairingBox);
    }

    [Fact]
    public async Task A_machine_that_needs_pairing_is_not_offered_a_way_out_of_it()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        Assert.True(window.ShowsPairingBox);
        Assert.False(window.ShowsCancelPairing);
    }

    // ---------- what ADL makes of this machine ----------

    [Theory]
    [InlineData("online", "Collecting and sending", TrayState.Working)]
    [InlineData("degraded", "Heartbeats are late", TrayState.NeedsAttention)]
    [InlineData("offline", "No heartbeats arriving", TrayState.Stopped)]
    [InlineData("cycle_stuck", "Alive but not scanning", TrayState.NeedsAttention)]
    [InlineData("unknown", "Nothing reported yet", TrayState.Unknown)]
    public async Task ADLs_verdict_reaches_the_window_in_words(
        string state, string said, TrayState tone)
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.FleetStatus = state;

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();
        await agent.HeartbeatLoop.BeatAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        // ADL stores and sends its own identifier. What a technician on a
        // country server reads should not be it.
        Assert.Equal(said, window.FleetStatus);
        Assert.DoesNotContain("_", window.FleetStatus);

        // And the colour beside the words, from the same vocabulary the tray
        // icon and the connection list use. Green, amber and red already mean
        // something on this screen; a fourth reading of them would be a
        // fourth thing to learn.
        Assert.Equal(tone, window.FleetTone);

        // The dot is drawn at all, because this machine is paired right now.
        Assert.True(window.ShowsPairedTo);
    }

    [Fact]
    public async Task A_state_this_build_has_never_heard_of_gets_no_colour()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.FleetStatus = "clock_skewed";

        await agent.PairAsync();
        await agent.HeartbeatLoop.BeatAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        // Grey is the absence of a verdict rather than a fourth one, which is
        // the rule the station grid already keeps. A build that does not know
        // what ADL just said has no verdict to offer, and guessing amber or
        // red at it would be this program inventing an opinion about a word
        // it cannot read.
        Assert.Equal(TrayState.Unknown, window.FleetTone);

        // The words are still there beside it -- see the test below.
        Assert.Equal("Clock skewed", window.FleetStatus);
    }

    [Fact]
    public async Task Offline_and_a_stuck_cycle_do_not_read_the_same()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        await agent.PairAsync();

        agent.Server.FleetStatus = "offline";

        await agent.HeartbeatLoop.BeatAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        var offline = window.FleetStatus;

        agent.Server.FleetStatus = "cycle_stuck";

        await agent.HeartbeatLoop.BeatAsync();
        await window.RefreshAsync();

        // Both mean nothing is arriving. The difference is whether somebody
        // has to walk to the machine, and it is the only thing these two
        // words have to carry.
        Assert.NotEqual(offline, window.FleetStatus);
    }

    [Fact]
    public async Task A_state_this_build_has_never_heard_of_is_still_readable()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.FleetStatus = "clock_skewed";

        await agent.PairAsync();
        await agent.HeartbeatLoop.BeatAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        // ADL owns this vocabulary and can add to it, and an agent in the
        // field is months behind whatever HQ deployed. The words are wrong;
        // an identifier on the screen would be worse.
        Assert.Equal("Clock skewed", window.FleetStatus);
    }

    // ---------- the header strip ----------

    [Fact]
    public async Task A_paired_machine_puts_the_address_on_the_first_line_and_the_verdict_last()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.FleetStatus = "online";

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();
        await agent.HeartbeatLoop.BeatAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        // The first line is the address, which the window draws as a link,
        // and the sentence that used to be there is empty -- because on this
        // one machine the headline is ADL's verdict at the bottom of the
        // strip rather than anything at the top of it.
        Assert.True(window.ShowsPairedTo);
        Assert.False(window.ShowsHeadline);
        Assert.Equal("", window.Headline);

        // Exactly one line in the header ever carries weight. These two are
        // the complement of each other so that it cannot be two, or none.
        Assert.NotEqual(window.ShowsHeadline, window.ShowsPairedTo);

        // Something to link to, and it goes where the row says it goes. Not
        // string equality: Uri normalises "http://host:8000" to a trailing
        // slash, and the row shows the address as the service holds it. A
        // difference bigger than that slash would be a link whose text and
        // destination disagree, which is the one thing a link must not do.
        Assert.NotNull(window.AdlLink);
        Assert.Equal(
            window.AdlUrl.TrimEnd('/'),
            window.AdlLink!.ToString().TrimEnd('/'));

        // And the facts between them, ADL's four and this machine's one.
        Assert.True(window.ShowsAdlFacts);
        Assert.NotEqual("-", window.DeviceName);
        Assert.NotEqual("-", window.AgentVersion);
        Assert.NotEqual("-", window.CheckInterval);
    }

    [Fact]
    public async Task The_header_says_how_long_ago_as_well_as_when()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();
        await agent.HeartbeatLoop.BeatAsync();

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        // Two readings of one moment, because they answer different
        // questions: the exact one is what somebody reads down a telephone or
        // matches against a log, and the span is the reading they do at a
        // glance. A heartbeat three days old should not need arithmetic.
        Assert.NotEqual("-", window.LastHeartbeat);
        Assert.NotEqual("", window.LastHeartbeatAgo);
        Assert.NotEqual("", window.LastSyncedAgo);

        // Just beaten and just synced, against the service's own clock. If
        // this ever reads in days, the header is aging one moment against
        // some other moment's "now".
        //
        // Brackets included, because they are part of the string rather than
        // runs in the window: a Run cannot be hidden, so a machine with no
        // heartbeat would otherwise show an empty pair of them.
        Assert.Equal("(moments ago)", window.LastHeartbeatAgo);
        Assert.Equal("(moments ago)", window.LastSyncedAgo);
    }

    [Fact]
    public async Task A_machine_ADL_has_never_beaten_for_says_nothing_about_when()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link);

        await window.RefreshAsync();

        // Empty rather than "-", "55 years ago", or a bare "()". These follow
        // a value on the same line, and a span invented for a moment that
        // never happened is the one thing worse than an absent one -- while
        // brackets around nothing are how a header comes to read
        // "Last heartbeat: - ()".
        Assert.Equal("", window.LastHeartbeatAgo);
        Assert.Equal("", window.LastSyncedAgo);
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

        // One connection, and ADL has linked nothing under it -- which is
        // the connection's own sentence to say now, not the tab's.
        Assert.False(window.ShowsMachineReason);
        Assert.True(window.ShowsConnectionReason);
        Assert.Contains(
            "has not linked any stations to this connection",
            window.SelectedConnection!.NoStationsReason);
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

        // The connection is on screen -- it came off the disk -- so this is
        // not the empty-list case. It is the untrustworthy-list case, and the
        // tab says so across both panes rather than letting a cached
        // connection with nothing under it blame an administrator for a
        // network outage.
        Assert.False(window.HasNoConnections);
        Assert.True(window.ShowsMachineReason);
        Assert.False(window.ShowsConnectionReason);

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

        Assert.True(window.HasNoConnections);
        Assert.True(window.ShowsMachineReason);
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
