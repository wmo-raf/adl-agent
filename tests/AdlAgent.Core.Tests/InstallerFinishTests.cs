using System.Xml.Linq;
using static AdlAgent.Core.Tests.WixSource;

namespace AdlAgent.Core.Tests;

/// <summary>
/// What a finished install leaves on the machine, and what it opens.
/// </summary>
/// <remarks>
/// A successful install used to have no visible outcome: a progress bar
/// closed, a service started, and a technician was left with a Start menu
/// entry they had no reason to look for and a tray that would not appear
/// until the next logon. That is the moment a phone call starts.
/// <para>
/// So the package now puts a shortcut on the desktop and the last screen
/// opens the window. Both are read out of the sources here for the same
/// reason <see cref="InstallerDialogTests"/> reads the dialog: none of it is
/// compiled, none of it is run by any other test, and the machine it is first
/// seen on is a country server somebody drove to.
/// </para>
/// <para>
/// The half this cannot answer -- whether Windows Installer does what its own
/// documentation says -- is <c>packaging/verify-msi-install.ps1</c>, which
/// installs the built package and looks for the shortcuts on disk.
/// </para>
/// </remarks>
public class InstallerFinishTests
{
    /// <summary>
    /// The custom action that opens the window: the util extension's, not
    /// one of ours.
    /// </summary>
    private const string ShellExec = "Wix4ShellExec_X64";

    /// <summary>
    /// Every place a technician might look for the window, they find it.
    /// </summary>
    /// <remarks>
    /// Three, and each answers a different person's question -- which one is
    /// whose is in <c>AdlAgent.wxs</c>, beside the shortcuts themselves. What
    /// is here is that all three are in the package and point at the tray,
    /// which is the part a source file cannot say twice and a compiler will
    /// not check once.
    /// </remarks>
    [Theory]
    [InlineData("AgentMenuFolder")]
    [InlineData("StartupFolder")]
    [InlineData("DesktopFolder")]
    public void The_tray_is_reachable_from_every_place_somebody_would_look(string folder)
    {
        var package = Load("AdlAgent.wxs");

        // A shortcut takes its folder from its own Directory attribute, or
        // from the component it sits in when it has none.
        var shortcut = Assert.Single(
            package.Descendants(Wxs + "Shortcut"),
            candidate =>
                ((string?)candidate.Attribute("Directory")
                 ?? (string?)candidate.Parent?.Attribute("Directory")) == folder);

        Assert.Equal("[INSTALLFOLDER]adl-agent-tray.exe", (string?)shortcut.Attribute("Target"));

        // And the folder is one this package declares, rather than a name
        // that would link and then land somewhere nobody looks.
        Assert.Contains(
            package.Descendants(),
            directory =>
                directory.Name.LocalName is "StandardDirectory" or "Directory" &&
                (string?)directory.Attribute("Id") == folder);
    }

    /// <summary>
    /// And uninstalling takes all three away again.
    /// </summary>
    /// <remarks>
    /// The state directory is <c>Permanent</c> on purpose -- a major upgrade
    /// must not take the device token with it -- and a shortcut that borrowed
    /// that reasoning would outlive the program it points at, which is a
    /// broken icon on a desktop for the rest of the machine's life.
    /// </remarks>
    [Fact]
    public void Nothing_a_shortcut_lives_in_survives_an_uninstall()
    {
        var components = Load("AdlAgent.wxs").Descendants(Wxs + "Shortcut")
            .Select(shortcut => shortcut.Ancestors(Wxs + "Component").Single())
            .Distinct()
            .ToList();

        Assert.NotEmpty(components);

        foreach (var component in components)
        {
            Assert.Null(component.Attribute("Permanent"));
        }
    }

    /// <summary>
    /// The last screen opens the window.
    /// </summary>
    /// <remarks>
    /// From the Exit dialog rather than from the install sequence, and that
    /// is the load-bearing part: a self-update runs
    /// <c>msiexec /i … /qn</c>, which has no user interface sequence at all,
    /// so an action hung off the finish screen cannot fire during an
    /// unattended upgrade. An agent that tried to open a window at three in
    /// the morning on a server with nobody logged on would be a new failure
    /// mode invented by a convenience.
    /// </remarks>
    [Fact]
    public void The_finish_screen_opens_the_tray()
    {
        var launch = Assert.Single(FinishButtonDoesOnAFreshInstall());

        Assert.Equal(ShellExec, (string?)launch.Attribute("Value"));

        // Pointed at the tray by the property that action reads, resolved to
        // wherever this package installed it rather than to a path spelled
        // out twice.
        var target = Load("AdlAgentUI.wxs").Descendants(Wxs + "Property")
            .Single(property => (string?)property.Attribute("Id") == "WixShellExecTarget");

        Assert.Equal("[#AgentTrayExe]", (string?)target.Attribute("Value"));

        // And it happens before the dialog set lets go of the install: a
        // DoAction published after the EndDialog is a button that ends the
        // installer and opens nothing.
        var end = Load("AdlAgentUI.wxs").Descendants(Wxs + "Publish").Single(publish =>
            (string?)publish.Attribute("Dialog") == "ExitDialog" &&
            (string?)publish.Attribute("Event") == "EndDialog");

        Assert.True(
            int.Parse((string)launch.Attribute("Order")!) < int.Parse((string)end.Attribute("Order")!),
            "The finish button ends the installer before it opens the window.");
    }

    /// <summary>
    /// A repair or an uninstall opens nothing.
    /// </summary>
    /// <remarks>
    /// Both come through the same Exit dialog. Opening the window after a
    /// repair is merely startling; opening it after an uninstall means
    /// launching a program the same transaction has just deleted.
    /// </remarks>
    [Fact]
    public void A_machine_that_already_had_this_is_not_shown_a_window()
    {
        var launch = Assert.Single(FinishButtonDoesOnAFreshInstall());

        Assert.False(MsiCondition.Evaluate(
            launch.Condition("Condition"),
            new Dictionary<string, string> { ["Installed"] = "00:00:00" }));
    }

    /// <summary>
    /// The action that opens the window is never scheduled.
    /// </summary>
    /// <remarks>
    /// This is the one that would break the fleet. <c>/qn</c> shows no dialog
    /// whatever a package asks for, so the way opening a window breaks an
    /// unattended upgrade is not the dialog -- it is the action behind it
    /// being scheduled as well as published, which would run on every machine
    /// that updates itself.
    /// <para>
    /// The action is the util extension's own, referenced rather than
    /// authored, so the package still declares no custom action of its own --
    /// which is what <see cref="InstallerDialogTests"/> checks and what
    /// <c>verify-msi-install.ps1</c> reads back out of the built database.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_action_that_opens_the_window_is_never_scheduled()
    {
        foreach (var file in new[] { "AdlAgent.wxs", "AdlAgentUI.wxs" })
        {
            var document = Load(file);

            // The execute sequence is InstallerDialogTests' to guard, and it
            // already does. This is the half that is newly load-bearing: the
            // action is reached from a button, so a package that also
            // sequenced it in the user interface would open a window on every
            // hand-run repair.
            Assert.Empty(document.Descendants(Wxs + "InstallUISequence"));

            // Every mention of it, anywhere in the sources: the reference that
            // pulls the action into the package, and the button that presses
            // it. Nothing else.
            foreach (var element in document.Descendants()
                         .Where(element => element.Attributes()
                             .Any(attribute => attribute.Value == ShellExec)))
            {
                Assert.Contains(
                    element.Name.LocalName,
                    new[] { "CustomActionRef", "Publish" });

                if (element.Name.LocalName == "Publish")
                {
                    Assert.Equal("DoAction", (string?)element.Attribute("Event"));
                }
            }
        }

        Assert.Single(
            Load("AdlAgentUI.wxs").Descendants(Wxs + "CustomActionRef"),
            reference => (string?)reference.Attribute("Id") == ShellExec);
    }

    /// <summary>What the Exit dialog's Finish button does on a fresh install.</summary>
    private static List<XElement> FinishButtonDoesOnAFreshInstall() =>
        Load("AdlAgentUI.wxs").Descendants(Wxs + "Publish")
            .Where(publish =>
                (string?)publish.Attribute("Dialog") == "ExitDialog" &&
                (string?)publish.Attribute("Control") == "Finish" &&
                (string?)publish.Attribute("Event") == "DoAction" &&
                MsiCondition.Evaluate(publish.Condition("Condition"), new Dictionary<string, string>()))
            .ToList();
}
