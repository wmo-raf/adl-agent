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
}
