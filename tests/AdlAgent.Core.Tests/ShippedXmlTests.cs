namespace AdlAgent.Core.Tests;

/// <summary>
/// That every XML document this repository ships is one a parser will take.
/// </summary>
/// <remarks>
/// The cheapest test in the suite, and it earns its place by what it costs to
/// find out any other way. Version 0.1.0 shipped an application manifest with
/// two hyphens inside an XML comment — written in the house style for a dash,
/// which the XML specification forbids. A manifest Windows cannot parse is a
/// program Windows will not start: not a degraded window, not a logged
/// warning, but a refusal at process creation, before any of the application
/// exists to report anything about itself. It cost a release, an install on a
/// country server, and a long evening, and it was findable in five
/// milliseconds by any parser on any operating system.
/// <para>
/// What makes that worth generalising is the shape of it rather than the
/// file. MSBuild and WiX parse their own inputs and fail loudly — the same
/// two hyphens in a <c>.csproj</c> produce <c>MSB4025</c> before anything
/// else happens — so those files were never the risk. The risk is XML that
/// nothing in the build reads: a manifest, an <c>app.config</c>, a WiX
/// fragment included only under a condition, an SVG. Each is authored in a
/// style that uses <c>--</c> freely and read by nothing until a machine in a
/// country nobody can reach refuses to start a program.
/// </para>
/// <para>
/// So this asserts all of it, including what the build already validates.
/// That is a judgement rather than an oversight: the alternative is an
/// exclusion list saying which files some other tool is responsible for, and
/// a list like that is wrong the moment somebody adds a file — which is
/// exactly the failure being guarded against. The cost of the choice is a
/// second failure beside a clearer one from MSBuild, on a day the build was
/// already broken.
/// </para>
/// <para>
/// This replaces <c>ApplicationManifestTests</c>, which asserted the tray's
/// manifest alone, and the <c>.wxs</c> case that lived in
/// <see cref="InstallerDialogTests"/>. Both are covered here by discovery
/// rather than by name.
/// </para>
/// </remarks>
public class ShippedXmlTests
{
    /// <summary>Every XML document in the repository, found rather than listed.</summary>
    public static TheoryData<string> ShippedXmlFiles
    {
        get
        {
            var files = new TheoryData<string>();

            foreach (var file in ShippedXml.Under(RepositoryRoot.Path))
            {
                files.Add(file);
            }

            return files;
        }
    }

    [Theory]
    [MemberData(nameof(ShippedXmlFiles))]
    public void Every_xml_document_this_repository_ships_is_well_formed(string file)
    {
        var why = ShippedXml.WhyNotWellFormed(
            Path.Combine(RepositoryRoot.Path, file), displayPath: file);

        Assert.True(why is null, why);
    }

    /// <summary>
    /// And the discovery that feeds it actually finds things.
    /// </summary>
    /// <remarks>
    /// A theory over an empty set is a passing test with no coverage in it,
    /// and nothing about the assertion above would say so. The named files
    /// are the three kinds this has to keep finding: one nothing in the build
    /// parses, one only WiX reads, and one that carries no XML declaration at
    /// all and so is found by its extension.
    /// </remarks>
    [Theory]
    [InlineData("src/AdlAgent.Tray/app.manifest")]
    [InlineData("packaging/msi/AdlAgent.wxs")]
    [InlineData("packaging/msi/AdlAgentUI.wxs")]
    [InlineData("assets/adl-logo.svg")]
    [InlineData("AdlAgent.slnx")]
    [InlineData("Directory.Build.props")]
    [InlineData("src/AdlAgent.Tray/MainWindow.xaml")]
    [InlineData("src/AdlAgent.Tray/AdlAgent.Tray.csproj")]
    public void The_search_finds_the_documents_it_is_for(string file)
    {
        Assert.Contains(file, ShippedXml.Under(RepositoryRoot.Path));
    }

    /// <summary>
    /// The manifest that shipped in 0.1.0 is refused, and the refusal says
    /// where.
    /// </summary>
    /// <remarks>
    /// The case that motivated all of this, kept as the bytes that shipped
    /// rather than fetched from <c>31dbd5c</c>: CI checks out one commit, so
    /// a test that read history would pass by not finding it.
    /// </remarks>
    [Fact]
    public void The_manifest_that_shipped_in_0_1_0_is_refused()
    {
        var manifest = Path.Combine(Path.GetTempPath(), $"adl-{Guid.NewGuid():N}.manifest");

        File.WriteAllText(manifest, ManifestFrom010);

        try
        {
            var why = ShippedXml.WhyNotWellFormed(manifest, displayPath: "app.manifest");

            Assert.NotNull(why);

            // The line the dash is on, so that the failure is an edit rather
            // than a search, and the reason rather than only a verdict.
            Assert.StartsWith("app.manifest(7,", why);
            Assert.Contains("--", why);
        }
        finally
        {
            File.Delete(manifest);
        }
    }

    /// <summary>
    /// A document with an extension nobody thought of is still covered.
    /// </summary>
    /// <remarks>
    /// The half of this that has to keep working without anybody
    /// remembering it exists. Run against a directory built here rather than
    /// against the repository, because the thing being asserted is what
    /// happens to a file that does not exist yet.
    /// </remarks>
    [Fact]
    public void A_file_nobody_anticipated_is_found_by_the_declaration_it_carries()
    {
        using var tree = new TemporaryTree();

        tree.Write("deploy/transform.mst.xmlish", "<?xml version=\"1.0\"?><t />");
        tree.Write("src/thing.csproj", "<Project />");
        tree.Write("notes.md", "<not xml, and not claiming to be>");

        Assert.Equal(
            ["deploy/transform.mst.xmlish", "src/thing.csproj"],
            ShippedXml.Under(tree.Path));
    }

    /// <summary>
    /// And what an editor or a build left behind is not.
    /// </summary>
    /// <remarks>
    /// Every one of these is in <c>.gitignore</c>, and <c>publish/</c> and
    /// <c>.dev-publish/</c> both hold a copy of the tray's manifest. Walking
    /// into them would report one mistake several times and would fail on one
    /// developer's machine over something no other machine has.
    /// </remarks>
    [Theory]
    [InlineData("obj")]
    [InlineData("bin")]
    [InlineData("publish")]
    [InlineData(".idea")]
    [InlineData(".dev-publish")]
    [InlineData("artifacts")]
    public void What_the_repository_does_not_ship_is_not_searched(string directory)
    {
        using var tree = new TemporaryTree();

        tree.Write($"{directory}/app.manifest", "<?xml version=\"1.0\"?><broken>");
        tree.Write($"src/{directory}/app.manifest", "<?xml version=\"1.0\"?><broken>");
        tree.Write("kept.xml", "<kept />");

        Assert.Equal(["kept.xml"], ShippedXml.Under(tree.Path));
    }

    /// <summary>A directory of files, gone when the test is.</summary>
    private sealed class TemporaryTree : IDisposable
    {
        public TemporaryTree() =>
            Directory.CreateDirectory(Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"adl-xml-{Guid.NewGuid():N}"));

        public string Path { get; }

        public void Write(string relative, string content)
        {
            var file = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>
    /// <c>src/AdlAgent.Tray/app.manifest</c> as it shipped in 0.1.0, down to
    /// the two hyphens on line 7.
    /// </summary>
    private const string ManifestFrom010 =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">

          <!--
            Two statements, both deliberate.

            asInvoker: the tray never elevates. It changes nothing on this machine --
            every setting it edits is written to ADL, and the only local thing it
            touches is a named pipe it is on the allow list for.
          -->
          <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
            <security>
              <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
                <requestedExecutionLevel level="asInvoker" uiAccess="false" />
              </requestedPrivileges>
            </security>
          </trustInfo>

        </assembly>
        """;
}
