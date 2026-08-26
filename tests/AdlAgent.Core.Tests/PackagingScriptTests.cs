using System.Text.RegularExpressions;
using static AdlAgent.Core.Tests.WixSource;

namespace AdlAgent.Core.Tests;

/// <summary>
/// That the scripts which make the package are still pointed at all of it.
/// </summary>
/// <remarks>
/// Two failures neither the compiler nor a review reliably catches, both of
/// which are found on a country server rather than here: a source file the
/// build does not build, and a toolset that moved under a release.
/// </remarks>
public class PackagingScriptTests
{
    /// <summary>
    /// One script builds the package, and it builds all of it.
    /// </summary>
    /// <remarks>
    /// One, on purpose: <c>pack.ps1</c> makes the release and
    /// <c>verify-msi-install.ps1</c> makes the package it upgrades that
    /// release with, and written out twice those two would drift. A source
    /// file missing from the second would be missing from the only package
    /// that ever proves an unattended upgrade keeps a machine's address.
    /// </remarks>
    [Fact]
    public void The_one_script_that_builds_the_package_builds_all_of_it()
    {
        var script = File.ReadAllText(Path.Combine(PackagingDirectory, "build-msi.ps1"));

        foreach (var source in Directory.EnumerateFiles(MsiDirectory, "*.wxs"))
        {
            Assert.Contains(Path.GetFileName(source), script);
        }

        Assert.Contains("-ext WixToolset.Util.wixext", script);
        Assert.Contains("-ext WixToolset.UI.wixext", script);

        foreach (var caller in new[] { "pack.ps1", "verify-msi-install.ps1" })
        {
            Assert.Contains(
                "build-msi.ps1",
                File.ReadAllText(Path.Combine(PackagingDirectory, caller)));
        }
    }

    /// <summary>
    /// And the toolset is pinned in one place, for all of it.
    /// </summary>
    /// <remarks>
    /// An extension pinned at a version of its own would put a dialog library
    /// and the compiler that links it in different toolsets, which is a class
    /// of breakage nobody looks for in a packaging script -- and it would
    /// change what a fleet installs without anything in this repository having
    /// changed.
    /// </remarks>
    [Fact]
    public void The_WiX_toolset_is_pinned_once()
    {
        var script = File.ReadAllText(Path.Combine(PackagingDirectory, "pack.ps1"));

        // Every extension the script installs, whether it names it or holds it
        // in a variable.
        var versions = Regex.Matches(script, @"(?:\.wixext|\$extension)/(\S+?)""")
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(versions);
        Assert.All(versions, version => Assert.Equal("$wixVersion", version));
        Assert.Single(Regex.Matches(script, @"\$wixVersion\s*="));
    }

    /// <summary>
    /// What a bare run builds is what a country installs.
    /// </summary>
    /// <remarks>
    /// The per-user tier is not built. No site has asked for it, it has never
    /// been run on a Windows machine, and a tier nobody installs is one whose
    /// packaging is proven by nothing -- so it is behind a switch, and the
    /// switch is off. Decision #262 is deferred rather than reversed: every
    /// line of the tier's code is still here and still under test, and
    /// <c>-WithPerUserTier</c> is what revives it.
    /// <para>
    /// Which way round the switch is matters more than it looks. It was
    /// <c>-MsiOnly</c> for a week, opt-in, which made the default -- both
    /// tiers -- a path nothing ever took, and a path nothing takes is the one
    /// that breaks unnoticed. The whole premise of there being one packaging
    /// script is that a person on a Windows machine can reproduce what shipped
    /// exactly as the build produced it, and that only holds while the bare
    /// command and the released packages are the same thing.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_release_is_the_MSI_and_the_per_user_tier_is_opt_in()
    {
        var script = File.ReadAllText(Path.Combine(PackagingDirectory, "pack.ps1"));

        Assert.Contains("$WithPerUserTier", script);

        // A [switch] is false unless it is passed, so what is asserted is that
        // nothing gives it one -- a default would turn the tier back on for
        // every caller at once, silently.
        Assert.DoesNotContain("$WithPerUserTier =", script);

        // The declaration, not the word: the parameter it replaced is worth
        // explaining in a comment, and a test that forbade naming it would be
        // a test against saying why.
        Assert.DoesNotContain("[switch] $MsiOnly", script);
    }

    /// <summary>
    /// And the workflow that actually decides it does not ask for more.
    /// </summary>
    /// <remarks>
    /// The script's default is only a default. What a fleet installs is what
    /// the packaging job attaches to a release, so the check that matters is
    /// on the invocation rather than on the parameter block.
    /// </remarks>
    [Fact]
    public void The_packaging_job_builds_what_a_release_is()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot.Path, ".github", "workflows", "ci.yml"));

        var invocations = Regex.Matches(workflow, @"\./packaging/pack\.ps1[^\r\n]*")
            .Select(match => match.Value)
            .ToList();

        Assert.NotEmpty(invocations);
        Assert.All(invocations, run => Assert.DoesNotContain("-WithPerUserTier", run));
    }
}
