using System.Xml;

namespace AdlAgent.Core.Tests;

/// <summary>
/// That the tray's application manifest is a document Windows can read.
/// </summary>
/// <remarks>
/// This is the cheapest test in the suite and it earns its place by what it
/// costs to find out any other way. A manifest Windows cannot parse is a
/// program Windows will not start: not a degraded window, not a logged
/// warning, but a refusal at process creation, before any of the application
/// exists to report anything about itself. Nothing else in this repository
/// reads the file. The compiler does not, the packaging job did not, and the
/// window is deliberately not automated, so a broken one travelled all the
/// way to an installer on a country server.
/// <para>
/// What broke it was two hyphens inside an XML comment, written in the house
/// style for a dash. The XML specification forbids them, Windows said
/// "Invalid Xml syntax", and it was right. That is a mistake anybody editing
/// this file will make again, and it is worth a millisecond on every test run
/// to be told on the machine where it was made rather than on the one machine
/// in the fleet that cannot open its window.
/// </para>
/// </remarks>
public class ApplicationManifestTests
{
    [Fact]
    public void The_trays_application_manifest_is_a_document_Windows_can_read()
    {
        var manifest = TrayManifestPath();

        // XmlReader rather than a string check: the rule being asserted is
        // "well-formed", and enumerating the ways a document can fail to be
        // is how you miss the next one.
        using var reader = XmlReader.Create(manifest);

        var failure = Record.Exception(() =>
        {
            while (reader.Read())
            {
            }
        });

        Assert.True(
            failure is null,
            $"{manifest} is not well-formed XML, so Windows will refuse to start the tray: {failure?.Message}");
    }

    private static string TrayManifestPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var manifest = Path.Combine(directory.FullName, "src", "AdlAgent.Tray", "app.manifest");

            if (File.Exists(manifest))
            {
                return manifest;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not find src/AdlAgent.Tray/app.manifest above the test binary. This test reads it.");
    }
}
