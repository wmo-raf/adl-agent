using AdlAgent.Core.State;
using AdlAgent.TestSupport;
using AdlAgent.Tray;
using AdlAgent.Windows;
using AdlAgent.Windows.Platform;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Changing where this machine reports, from the window rather than from a
/// command prompt.
/// </summary>
/// <remarks>
/// The window is a thin caller: everything that decides anything -- what a
/// usable address is, what happens to the token, when the service comes back
/// -- is <see cref="SetUrl"/>, which a machine with no desktop runs the same
/// way. What is under test here is the handful of decisions that are the
/// window's own: when to offer this at all, what to send, and what to do with
/// each of the three answers Windows can give.
/// <para>
/// The consent prompt itself is the seam. A test that raised one would need
/// somebody to click it, so <see cref="RecordingAddressChange"/> stands where
/// the operating system does and records what it was asked for -- including
/// the case that matters most, which is the one where nobody asks it
/// anything.
/// </para>
/// </remarks>
public class AdlAddressTests
{
    private const string Elsewhere = "https://adl.elsewhere.example.org";

    // ---------- when the window offers this at all ----------

    [Fact]
    public async Task The_ADL_row_offers_a_way_to_change_the_address()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link, new RecordingAddressChange());

        // Nothing until the service has answered: there is no address to
        // put in the box, and a button that opened onto an empty one would
        // be offering to point this machine at nothing.
        Assert.False(window.ShowsChangeAdl);
        Assert.Null(window.BeginChangingAdl());

        await window.RefreshAsync();

        Assert.True(window.ShowsChangeAdl);
    }

    [Fact]
    public async Task A_machine_with_no_address_is_offered_the_same_button()
    {
        await using var agent = Unconfigured();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link, new RecordingAddressChange());

        await window.RefreshAsync();

        // The state the hint beneath this row is about. Until now it named a
        // command and left; the button is the same command with the typing
        // done.
        Assert.False(window.IsConfigured);
        Assert.True(window.ShowsChangeAdl);

        var change = window.BeginChangingAdl()!;

        Assert.Equal("", change.Address);
    }

    [Fact]
    public async Task The_dialog_opens_on_the_address_this_machine_reports_to()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link, new RecordingAddressChange());

        await window.RefreshAsync();

        var change = window.BeginChangingAdl()!;

        // The address itself, not the sentence the row draws for a machine
        // that has none: this is a box somebody edits.
        Assert.Equal(agent.Server.BaseAddress.ToString().TrimEnd('/'), change.Address);

        // Off, and the dialog says what leaving it off will cost.
        Assert.False(change.KeepPairing);
        Assert.Contains("pair", change.Consequence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_station_list_stops_moving_while_the_dialog_is_open()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, "C:\\VendorData", "*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link, new RecordingAddressChange());

        await window.RefreshAsync();

        var change = window.BeginChangingAdl()!;

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, "C:\\Moved", "*.dat"));

        await agent.Configuration.RefreshAsync();
        await window.RefreshAsync();

        Assert.Equal("C:\\VendorData", window.SelectedStation!.LocalFolderPath);

        change.Done();

        await window.RefreshAsync();

        Assert.Equal("C:\\Moved", window.SelectedStation!.LocalFolderPath);
    }

    // ---------- what never reaches the prompt ----------

    [Theory]
    [InlineData("http://adl.example.org")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public async Task An_address_the_agent_would_refuse_is_refused_before_any_prompt(string typed)
    {
        var windows = new RecordingAddressChange();

        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link, windows);

        await window.RefreshAsync();

        var change = window.BeginChangingAdl()!;

        change.Address = typed;

        Assert.Equal(AddressChangeOutcome.Refused, await change.SaveAsync());

        // Nothing was raised. Asking a technician's administrator for a
        // password and then telling them the address was never usable is the
        // one outcome this window can prevent by itself.
        Assert.Empty(windows.Asked);
        Assert.NotEqual("", change.Answer);
    }

    [Fact]
    public async Task A_refusal_is_the_reason_the_agent_itself_would_give()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link, new RecordingAddressChange());

        await window.RefreshAsync();

        var change = window.BeginChangingAdl()!;

        change.Address = "http://adl.example.org";

        await change.SaveAsync();

        Assert.Contains("https", change.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_button_is_grey_until_the_address_is_a_different_one()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link, new RecordingAddressChange());

        await window.RefreshAsync();

        var change = window.BeginChangingAdl()!;

        // A prompt raised to write the address that is already there would
        // cost an administrator's password and, with the box below it
        // unticked, would unpair a working machine for nothing.
        Assert.False(change.SaveCommand.CanExecute(null));
        Assert.NotEqual("", change.Answer);

        change.Address = Elsewhere;

        Assert.True(change.SaveCommand.CanExecute(null));
    }

    // ---------- what the prompt is asked for ----------

    [Fact]
    public async Task Saving_asks_for_the_address_that_was_typed_with_the_pairing_cleared()
    {
        var windows = new RecordingAddressChange();

        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link, windows);

        await window.RefreshAsync();

        var change = window.BeginChangingAdl()!;

        // Trimmed, because a pasted address arrives with whatever was around
        // it on the page it was copied from.
        change.Address = $"  {Elsewhere}  ";

        Assert.Equal(AddressChangeOutcome.Changed, await change.SaveAsync());

        Assert.Equal([(Elsewhere, false)], windows.Asked);
    }

    [Fact]
    public async Task The_same_ADL_at_a_new_address_keeps_the_pairing_when_somebody_says_so()
    {
        var windows = new RecordingAddressChange();

        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link, windows);

        await window.RefreshAsync();

        var change = window.BeginChangingAdl()!;

        change.Address = Elsewhere;
        change.KeepPairing = true;

        Assert.Equal(AddressChangeOutcome.Changed, await change.SaveAsync());

        Assert.Equal([(Elsewhere, true)], windows.Asked);

        // And the dialog said which of the two it was about to do.
        Assert.DoesNotContain("pair this machine again", change.Consequence,
            StringComparison.OrdinalIgnoreCase);
    }

    // ---------- and what comes back ----------

    [Fact]
    public async Task Declining_the_prompt_changes_nothing_and_the_window_says_so()
    {
        var windows = new RecordingAddressChange
        {
            Answer = new AddressChange(
                AddressChangeOutcome.Declined,
                "Windows was not given permission to change this machine's address."),
        };

        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link, windows);

        await window.RefreshAsync();

        var change = window.BeginChangingAdl()!;

        change.Address = Elsewhere;

        Assert.Equal(AddressChangeOutcome.Declined, await change.SaveAsync());

        // Said, rather than a window that closes as though it had worked.
        Assert.Contains("Nothing has been changed", window.Message);
        Assert.Contains("Nothing has been changed", change.Answer);
    }

    [Fact]
    public async Task A_change_the_verb_could_not_finish_is_reported_in_its_own_words()
    {
        var windows = new RecordingAddressChange
        {
            Answer = new AddressChange(
                AddressChangeOutcome.Refused,
                "adl-agent set-url exited with 1."),
        };

        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link, windows);

        await window.RefreshAsync();

        var change = window.BeginChangingAdl()!;

        change.Address = Elsewhere;

        Assert.Equal(AddressChangeOutcome.Refused, await change.SaveAsync());
        Assert.Contains("exited with 1", change.Answer);
    }

    [Fact]
    public async Task A_change_that_cleared_the_pairing_sends_the_technician_to_the_code_box()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, "C:\\VendorData", "*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link, new RecordingAddressChange());

        await window.RefreshAsync();

        // A working machine opens on its stations. After this it is an
        // unpaired machine, whatever the service is still holding while it
        // restarts, and the one thing to do about it is on the other tab.
        Assert.Equal(TrayTabs.Stations, window.SelectedTab);

        var change = window.BeginChangingAdl()!;

        change.Address = Elsewhere;

        await change.SaveAsync();

        Assert.Equal(TrayTabs.Status, window.SelectedTab);
        Assert.Contains(Elsewhere, window.Message);
        Assert.Contains("pairing code", window.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_repointed_machine_is_unpaired_on_the_page_before_the_service_says_so()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link, new RecordingAddressChange());

        await window.RefreshAsync();

        Assert.True(window.IsPaired);

        var change = window.BeginChangingAdl()!;

        change.Address = Elsewhere;

        await change.SaveAsync();

        // Not on the next poll. The service is restarting, and a poll that
        // cannot reach it keeps the last snapshot -- so "Paired" would stand
        // beside a line saying the pairing was cleared, for as long as the
        // machine took to come back. The window would be describing a machine
        // it knows this is not.
        Assert.False(window.IsPaired);
        Assert.Equal("Not paired yet", window.PairingLine);
        Assert.True(window.ShowsPairingBox);
        Assert.Equal(Elsewhere, window.AdlUrl);
        Assert.Equal(NextStepKind.NotPaired, window.NextStep.Kind);
    }

    [Fact]
    public async Task An_instance_that_only_moved_domain_is_still_the_paired_one()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link, new RecordingAddressChange());

        await window.RefreshAsync();

        var change = window.BeginChangingAdl()!;

        change.Address = Elsewhere;
        change.KeepPairing = true;

        await change.SaveAsync();

        // The address moved and nothing else did. A machine whose token was
        // deliberately kept must not be told to pair again.
        Assert.Equal(Elsewhere, window.AdlUrl);
        Assert.True(window.IsPaired);
        Assert.Equal(agent.Server.Device.Name, window.DeviceName);
    }

    [Fact]
    public async Task A_machine_that_had_no_address_stops_saying_so_the_moment_it_has_one()
    {
        await using var agent = Unconfigured();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link, new RecordingAddressChange());

        await window.RefreshAsync();

        Assert.False(window.IsConfigured);

        var change = window.BeginChangingAdl()!;

        change.Address = Elsewhere;

        await change.SaveAsync();

        // The hint under this row names a command for a machine with no
        // address. Leaving it there over an address somebody has just given
        // it would send them to do again what they have just done.
        Assert.True(window.IsConfigured);
        Assert.False(window.NeedsConfiguring);
        Assert.Equal("", window.ConfigurationHint);
        Assert.Equal(Elsewhere, window.AdlUrl);
    }

    [Fact]
    public async Task A_change_that_kept_the_pairing_leaves_the_technician_where_they_were()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, "C:\\VendorData", "*.dat"));

        await agent.PairAsync();
        await agent.Configuration.RefreshAsync();

        var window = new ShellViewModel(serving.Link, new RecordingAddressChange());

        await window.RefreshAsync();

        var change = window.BeginChangingAdl()!;

        change.Address = Elsewhere;
        change.KeepPairing = true;

        await change.SaveAsync();

        Assert.Equal(TrayTabs.Stations, window.SelectedTab);
        Assert.Contains(Elsewhere, window.Message);
    }

    // ---------- the thing it is a caller of ----------

    [Fact]
    public async Task What_the_window_launches_is_the_verb_a_machine_with_no_desktop_runs()
    {
        var state = Directory.CreateTempSubdirectory("adl-agent-tray-set-url").FullName;

        try
        {
            var store = new InMemoryAgentStateStore();
            var service = new RecordingServiceControl();

            using var output = new StringWriter();

            // The command line the tray hands to Windows, run against the
            // verb itself. Nothing in the window may drift into arguments
            // the verb would answer with its usage text.
            var exitCode = await new SetUrl(state, store, service, elevated: true)
                .RunAsync(
                    ElevatedAddressChange.ArgumentsFor(Elsewhere, keepPairing: true), output);

            Assert.Equal(0, exitCode);
            Assert.Contains(
                $"AdlBaseUrl={Elsewhere}",
                File.ReadAllText(MachineSettings.PathIn(state)));
            Assert.Equal(["stop", "start"], service.Calls);
        }
        finally
        {
            Directory.Delete(state, recursive: true);
        }
    }

    [Fact]
    public async Task A_tray_that_cannot_find_the_agent_raises_no_prompt_for_it()
    {
        var elsewhere = Directory.CreateTempSubdirectory("adl-agent-tray-alone").FullName;

        try
        {
            // A tray running from somewhere the service is not. Raising a
            // consent prompt for a file that does not exist would spend an
            // administrator's password on nothing.
            var answer = await new ElevatedAddressChange(Path.Combine(elsewhere, "adl-agent.exe"))
                .RequestAsync(Elsewhere, keepPairing: false);

            Assert.Equal(AddressChangeOutcome.Refused, answer.Outcome);
            Assert.Contains(SetUrl.Verb, answer.Detail);
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public void The_pairing_is_only_kept_when_the_box_is_ticked()
    {
        Assert.Equal(
            [SetUrl.Verb, Elsewhere],
            ElevatedAddressChange.ArgumentsFor(Elsewhere, keepPairing: false));

        Assert.Equal(
            [SetUrl.Verb, Elsewhere, SetUrl.KeepPairingSwitch],
            ElevatedAddressChange.ArgumentsFor(Elsewhere, keepPairing: true));
    }

    // ---------- helpers ----------

    [Fact]
    public async Task A_machine_with_no_address_offers_no_link_to_open()
    {
        await using var agent = Unconfigured();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link, new RecordingAddressChange());

        await window.RefreshAsync();

        // The address this machine shows is a sentence, not an address. The
        // header's link binds to the Uri rather than to that string, so that
        // WPF is never asked to convert it: bindings are evaluated on
        // collapsed elements too, and the conversion would fail once a poll
        // for as long as the tray ran -- into the binding log that exists to
        // make a real mistake findable.
        Assert.False(window.IsConfigured);
        Assert.Null(window.AdlLink);

        // The row is not drawn here anyway. This is belt to that braces: the
        // gate is about what is worth showing, and the null is about what
        // must never reach a browser.
        Assert.False(window.ShowsPairedTo);
        Assert.True(window.ShowsHeadline);
    }

    [Fact]
    public async Task A_configured_address_is_something_a_browser_can_open()
    {
        await using var agent = new AgentHarness();
        using var serving = await ServedAgent.ServingAsync(agent);

        var window = new ShellViewModel(serving.Link, new RecordingAddressChange());

        await window.RefreshAsync();

        // http or https and nothing else. What is on the other side of this
        // is ShellExecute, and a file: or ms-settings: reaching it is a
        // different kind of thing to hand the shell than the web address the
        // header says this row is.
        Assert.NotNull(window.AdlLink);
        Assert.True(
            window.AdlLink!.Scheme is "http" or "https",
            $"{window.AdlLink} is not something to hand a browser.");
    }

    private static AgentHarness Unconfigured() =>
        new(settings: new Dictionary<string, string?> { ["Agent:AdlBaseUrl"] = "" });
}

/// <summary>
/// Windows' consent prompt, with the consent taken out.
/// </summary>
/// <remarks>
/// It records what it was asked for rather than only how often, because the
/// two things the window decides are exactly those: the address it sends and
/// whether it says to keep the pairing. And <see cref="Asked"/> being empty
/// is an assertion in its own right -- a refusal the window made for itself
/// is a prompt nobody was troubled with.
/// </remarks>
internal sealed class RecordingAddressChange : IAddressChange
{
    private readonly List<(string Url, bool KeepPairing)> _asked = [];

    public IReadOnlyList<(string Url, bool KeepPairing)> Asked => _asked;

    /// <summary>What Windows says. A change that went through, unless set.</summary>
    public AddressChange Answer { get; set; } =
        new(AddressChangeOutcome.Changed, "");

    public Task<AddressChange> RequestAsync(
        string adlBaseUrl, bool keepPairing, CancellationToken cancellationToken = default)
    {
        _asked.Add((adlBaseUrl, keepPairing));

        return Task.FromResult(Answer);
    }
}
