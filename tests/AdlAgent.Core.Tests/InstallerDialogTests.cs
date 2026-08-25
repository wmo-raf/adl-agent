using System.Xml.Linq;
using static AdlAgent.Core.Tests.WixSource;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The one screen the MSI shows, and the path it must not disturb.
/// </summary>
/// <remarks>
/// A dialog is the part of a product nothing else in a repository can check.
/// It is not compiled -- WiX never parses a control condition, it stores the
/// string -- it is not run by any test, and the machine it is first seen on
/// is a country server somebody drove to. Everything asserted here was, until
/// this file, something that would have been discovered by an NMHS.
/// <para>
/// Three questions, and they are different questions. Does the screen accept
/// what the agent accepts, and refuse what it refuses? Does it say why, every
/// time it refuses? And is it still out of the way of an install that was
/// given an address or shown to nobody -- which is how every machine in the
/// fleet updates itself at three in the morning?
/// </para>
/// <para>
/// What runs on Windows, against a package this actually built, is
/// <c>packaging/verify-msi-install.ps1</c>. This runs everywhere and reads the
/// sources; that one installs the thing and looks at the file it wrote.
/// </para>
/// </remarks>
public class InstallerDialogTests
{
    /// <summary>
    /// Addresses on which the screen and the service must give the same
    /// answer.
    /// </summary>
    /// <remarks>
    /// What a person types, and what a person mistypes. The two rules are
    /// written in different languages -- one in C# against
    /// <see cref="Uri"/>, one in Windows Installer's condition syntax -- and
    /// this table is the contract between them.
    /// </remarks>
    public static TheoryData<string> AddressesBothMustAgreeOn =>
    [
        // Accepted by both.
        "https://adl.example.org",
        "https://adl.example.org/",
        "https://adl.example.org:8443",
        "https://adl.example.org/adl",
        "https://adl.example.org?next=1",
        "HTTPS://adl.example.org",
        "https://a",
        "https://192.168.1.1",
        "http://localhost",
        "http://localhost/",
        "http://LOCALHOST",
        "http://localhost:8000",
        "http://127.0.0.1",
        "http://127.0.0.1:8000",
        "http://[::1]",
        "http://[::1]:8000",

        // Refused by both.
        "",
        "   ",
        "adl.example.org",
        "localhost",
        "http://adl.example.org",
        "http://adl.example.org/",
        "ftp://adl.example.org",
        "htps://adl.example.org",
        "https://",
        "https:///",
        "https:/adl.example.org",
        "https://adl example.org",
        "https://adl.example.org /extra",
        "https://adl.example.org ",
        @"\\server\share",
        "http://localhostage.example.org",
        "http://127.0.0.1.example.org",
    ];

    [Theory]
    [MemberData(nameof(AddressesBothMustAgreeOn))]
    public void The_screen_gives_the_same_answer_as_the_agent(string url)
    {
        var accepted = new AgentOptions { AdlBaseUrl = url }.IsConfigured;

        Assert.Equal(accepted, Evaluate(NextIsAvailable(), url));
    }

    /// <summary>
    /// Every refusal is a refusal somebody can act on.
    /// </summary>
    /// <remarks>
    /// A Next button that will not light up and no sentence saying why is the
    /// worst outcome this screen could have: worse than the command line it
    /// replaced, because at least that one installed. The message and the
    /// button are driven by the same rule, and this is what says so.
    /// <para>
    /// Except for an untouched field. An error on a screen nobody has typed
    /// into yet is not a refusal, it is an accusation.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AddressesBothMustAgreeOn))]
    public void A_refused_address_is_always_explained(string url)
    {
        var dialog = DialogControls();

        var available = Evaluate(NextIsAvailable(), url);
        var explained = Evaluate(dialog["Problem"].Condition("ShowCondition"), url);

        Assert.Equal(url.Length > 0 && !available, explained);

        // And the other direction of each pair, because a Windows Installer
        // control condition only ever fires the action it names: a control
        // that nothing hides again stays as the first keystroke left it.
        Assert.Equal(!available, Evaluate(dialog["Next"].Condition("DisableCondition"), url));
        Assert.Equal(!explained, Evaluate(dialog["Problem"].Condition("HideCondition"), url));
    }

    /// <summary>
    /// What the screen still lets through, stated rather than discovered.
    /// </summary>
    /// <remarks>
    /// Windows Installer's condition syntax has no regular expressions and
    /// cannot take a string apart, so "parseable" can only be approximated:
    /// a scheme, something after it, and no spaces. <see cref="Uri"/> is a
    /// whole parser, and it refuses things this cannot see -- a doubled dot,
    /// a port that is not a number, a host that is only punctuation.
    /// <para>
    /// This is here so that the gap is a decision somebody made rather than
    /// something a country finds. A machine that gets through here is not
    /// silent about it: the service reports that its address is not usable,
    /// and the tray's Status tab is where a technician sees so, seconds after
    /// the install rather than never.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("https://a..b")]
    [InlineData("https://:8443")]
    [InlineData("https://adl.example.org:notaport")]
    [InlineData("https://.")]
    public void The_screen_accepts_a_few_addresses_the_agent_will_still_refuse(string url)
    {
        Assert.False(new AgentOptions { AdlBaseUrl = url }.IsConfigured);
        Assert.True(Evaluate(NextIsAvailable(), url));
    }

    /// <summary>
    /// And what it refuses that the agent would have taken.
    /// </summary>
    /// <remarks>
    /// The same limit from the other side. Loopback is spelled out as three
    /// hosts because the syntax cannot ask whether an address is loopback, and
    /// leading whitespace is refused here although <see cref="Uri"/> would
    /// trim it. Both are addresses of a test fixture rather than of a national
    /// instance, both still work when passed as <c>ADLURL=</c> on a command
    /// line, and being too strict on this screen costs a developer a command
    /// line while being too loose costs a country an install.
    /// </remarks>
    [Theory]
    [InlineData("http://127.0.0.11")]
    [InlineData(" https://adl.example.org")]
    public void The_screen_refuses_a_few_addresses_the_agent_would_have_taken(string url)
    {
        Assert.True(new AgentOptions { AdlBaseUrl = url }.IsConfigured);
        Assert.False(Evaluate(NextIsAvailable(), url));
    }

    /// <summary>
    /// The field feeds the property the command line has always set.
    /// </summary>
    [Fact]
    public void The_screen_writes_the_address_into_ADLURL()
    {
        var field = DialogControls()["Url"];

        Assert.Equal("Edit", (string?)field.Attribute("Type"));
        Assert.Equal("ADLURL", (string?)field.Attribute("Property"));
    }

    /// <summary>
    /// Somebody who has already given an address is not asked for it.
    /// </summary>
    /// <remarks>
    /// <c>msiexec /i AdlAgent.msi ADLURL=…</c> is how a country deploys a room
    /// full of machines, and it is what every existing document uses. A screen
    /// that stopped that install to ask for a value it had just been given
    /// would make the command line worse than it was before the screen
    /// existed.
    /// </remarks>
    [Theory]
    [InlineData("", "AdlUrlDlg")]
    [InlineData("https://adl.example.org", "VerifyReadyDlg")]
    public void The_welcome_screen_leads_where_the_command_line_left_off(string given, string expected)
    {
        var machine = new Dictionary<string, string> { ["ADLURL"] = given };

        var taken = Load("AdlAgentUI.wxs").Descendants(Wxs + "Publish")
            .Where(publish =>
                (string?)publish.Attribute("Dialog") == "WelcomeDlg" &&
                (string?)publish.Attribute("Control") == "Next" &&
                (string?)publish.Attribute("Event") == "NewDialog" &&
                MsiCondition.Evaluate(publish.Condition("Condition"), machine))
            .Select(publish => (string?)publish.Attribute("Value"))
            .ToList();

        // One, because two would mean the screen a technician sees depends on
        // the order Windows Installer happens to evaluate them in.
        Assert.Equal(expected, Assert.Single(taken));
    }

    /// <summary>
    /// An upgrade that is passed nothing changes nothing.
    /// </summary>
    /// <remarks>
    /// This is the one that would break the fleet. The agent installs a new
    /// MSI over itself with <c>/qn</c> and no properties, and the component
    /// that writes <c>agent.ini</c> is conditioned on <c>ADLURL</c> so that
    /// this leaves the setting alone. Adding a dialog is the change most
    /// likely to have quietly given <c>ADLURL</c> a value -- a default, a
    /// registry search, a <c>SetProperty</c> -- on a path where nobody would
    /// see it happen.
    /// </remarks>
    [Fact]
    public void An_upgrade_that_is_passed_no_properties_leaves_the_address_alone()
    {
        var package = Load("AdlAgent.wxs");

        var property = package.Descendants(Wxs + "Property")
            .Single(p => (string?)p.Attribute("Id") == "ADLURL");

        Assert.Null(property.Attribute("Value"));
        Assert.Empty(property.Elements());

        // Nor from anywhere else. A registry search into ADLURL is the
        // tempting one -- it would offer a machine's existing address back to
        // somebody upgrading it by hand -- and AppSearch runs in the execute
        // sequence too, so it would set the property on every silent upgrade
        // in the fleet.
        Assert.DoesNotContain(
            package.Descendants(Wxs + "RegistrySearch"),
            search => (string?)search.Parent?.Attribute("Id") == "ADLURL");

        var component = package.Descendants(Wxs + "Component")
            .Single(c => (string?)c.Attribute("Id") == "AgentAdlUrl");

        Assert.Equal("yes", (string?)component.Attribute("Permanent"));

        // Nothing set it, so the component that would rewrite the setting is
        // not installed, so what is on disk survives.
        Assert.False(MsiCondition.Evaluate(
            component.Condition("Condition"), new Dictionary<string, string>()));
    }

    /// <summary>
    /// None of this is reachable from a silent install.
    /// </summary>
    /// <remarks>
    /// <c>/qn</c> shows no dialog whatever the package asks for, so the way
    /// this breaks is not a dialog appearing -- it is something scheduled
    /// beside the dialog that also runs without one. A custom action or an
    /// execute-sequence entry added while wiring up a screen would run on
    /// every unattended upgrade in the fleet.
    /// </remarks>
    [Fact]
    public void Nothing_the_screen_needs_runs_when_there_is_no_screen()
    {
        foreach (var file in new[] { "AdlAgent.wxs", "AdlAgentUI.wxs" })
        {
            var document = Load(file);

            Assert.Empty(document.Descendants(Wxs + "CustomAction"));
            Assert.Empty(document.Descendants(Wxs + "SetProperty"));
            Assert.Empty(document.Descendants(Wxs + "InstallExecuteSequence"));
        }
    }

    /// <summary>
    /// The dialog set leads somewhere, from every button on the screen.
    /// </summary>
    /// <remarks>
    /// A dialog with no way forward is an installer a technician has to kill
    /// from Task Manager, and it is invisible in a source file: the button
    /// exists, it simply publishes nothing. Windows Installer will not say so
    /// either -- it draws the button and does nothing when it is pressed.
    /// </remarks>
    [Fact]
    public void Every_button_on_the_screen_goes_somewhere()
    {
        var published = Load("AdlAgentUI.wxs").Descendants(Wxs + "Publish")
            .Where(publish => (string?)publish.Attribute("Dialog") == "AdlUrlDlg")
            .Select(publish => (string?)publish.Attribute("Control"))
            .ToHashSet();

        Assert.Contains("Back", published);
        Assert.Contains("Next", published);

        // Cancel publishes from inside its own control rather than from the
        // dialog set, the way every stock WiX dialog does it.
        Assert.NotEmpty(DialogControls()["Cancel"].Elements(Wxs + "Publish"));
    }

    /// <summary>When the Next button on the address screen is available.</summary>
    private static string NextIsAvailable() =>
        DialogControls()["Next"].Condition("EnableCondition");

    private static bool Evaluate(string condition, string url) =>
        MsiCondition.Evaluate(condition, "ADLURL", url);

    /// <summary>The controls on the address screen, by their ids.</summary>
    private static Dictionary<string, XElement> DialogControls() =>
        Load("AdlAgentUI.wxs")
            .Descendants(Wxs + "Dialog")
            .Single(dialog => (string?)dialog.Attribute("Id") == "AdlUrlDlg")
            .Elements(Wxs + "Control")
            .ToDictionary(control => (string)control.Attribute("Id")!);
}
