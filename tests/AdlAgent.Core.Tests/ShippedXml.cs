using System.Xml;

namespace AdlAgent.Core.Tests;

/// <summary>
/// Every XML document this repository ships, and whether a parser will take
/// it.
/// </summary>
/// <remarks>
/// Split from the test that asserts it so that both halves can be run against
/// a directory of known contents. A discovery that quietly found nothing would
/// make a passing test out of no coverage at all, and that is not a failure
/// any assertion over the real tree can see.
/// </remarks>
internal static class ShippedXml
{
    /// <summary>
    /// Directories whose contents are not this repository's to be right
    /// about.
    /// </summary>
    /// <remarks>
    /// Build output, packaging output, and whatever an editor or a local
    /// deployment leaves behind. Every one of these is in <c>.gitignore</c>,
    /// and several can hold copies of files that are checked here at their
    /// source -- <c>publish/</c> and <c>.dev-publish/</c> both carry the
    /// tray's manifest -- so a walk that did not skip them would report the
    /// same problem several times, and would fail on one developer's machine
    /// over something no other machine has.
    /// <para>
    /// Public so that the test which proves they are skipped is driven by
    /// this list rather than by a second copy of it. A directory added here
    /// and forgotten there would be a test that no longer covers what it
    /// says it covers.
    /// </para>
    /// </remarks>
    public static readonly string[] NotOurs =
    [
        ".git", ".idea", ".vs", ".vscode", ".dev-publish",
        "bin", "obj", "publish", "artifacts", "TestResults",
    ];

    /// <summary>
    /// What is XML by its name.
    /// </summary>
    /// <remarks>
    /// Two ways in, because neither alone finds everything, and this is the
    /// belt rather than the braces. An XML declaration is optional, so a
    /// document that omits one is found only by its extension: today that is
    /// every <c>.csproj</c>, which opens on <c>&lt;Project&gt;</c>, the
    /// <c>.slnx</c>, <c>Directory.Build.props</c>, every <c>.xaml</c> in the
    /// tray, and <c>assets/adl-logo.svg</c>, which opens straight on
    /// <c>&lt;svg&gt;</c>. The rest of the list is formats that usually do
    /// declare themselves but need not, which is the whole reason not to
    /// depend on the declaration alone.
    /// <para>
    /// <c>.axaml</c> and <c>.wixproj</c> are here for files this repository
    /// does not have yet and would not notice the absence of: a tray for the
    /// Linux head would be authored in the first, and the second opens on
    /// <c>&lt;Project&gt;</c> exactly as a <c>.csproj</c> does. Both are the
    /// case this test exists for -- XML nothing in the build reads, added by
    /// somebody who never heard of this file.
    /// </para>
    /// <para>
    /// An <c>.mst</c> transform is not here, although it is the kind of thing
    /// that gets authored for a tool CI never runs. A built one is a binary
    /// compound file rather than XML, so there is nothing here to assert
    /// about it; if one is ever generated from an XML source, that source
    /// will be found by whatever it is named.
    /// </para>
    /// <para>
    /// The other way in is <see cref="DeclaresItselfXml"/>, which covers a
    /// file with an extension nobody thought of -- including the one that
    /// makes this list wrong next year.
    /// </para>
    /// </remarks>
    private static readonly string[] XmlExtensions =
    [
        ".xml", ".xaml", ".axaml", ".manifest", ".config", ".svg",
        ".csproj", ".wixproj", ".props", ".targets", ".slnx", ".nuspec",
        ".wxs", ".wxi", ".wxl", ".resx", ".xsd", ".xsl", ".xslt",
    ];

    /// <summary>
    /// Every XML document under <paramref name="root"/>, as paths relative to
    /// it.
    /// </summary>
    /// <remarks>
    /// Relative, and with forward slashes, so that the theory this feeds
    /// names the same case on every operating system and reads the way the
    /// repository is written about.
    /// </remarks>
    public static IEnumerable<string> Under(string root) =>
        Walk(new DirectoryInfo(root))
            .Where(IsXml)
            .Select(file => System.IO.Path.GetRelativePath(root, file.FullName).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal);

    /// <summary>
    /// Why a parser will not take the document at <paramref name="relative"/>
    /// under <paramref name="root"/>, or <c>null</c> when it will.
    /// </summary>
    /// <remarks>
    /// <see cref="XmlReader"/> rather than a search for the mistake that has
    /// already been made once: the rule being asserted is "well-formed", and
    /// enumerating the ways a document can fail to be is how you miss the
    /// next one.
    /// <para>
    /// The message is in the shape a compiler uses -- <c>path(line,column):
    /// reason</c> -- because it is read in the same places, and because a
    /// failure that names the line is the difference between an edit and a
    /// search. The path it names is the relative one, so that a failure reads
    /// the same in every checkout and says nothing about whose machine found
    /// it. Taking the two halves separately rather than a joined path and a
    /// label is what keeps those two facts from disagreeing.
    /// </para>
    /// </remarks>
    public static string? WhyNotWellFormed(string root, string relative)
    {
        using var reader = XmlReader.Create(System.IO.Path.Combine(root, relative));

        try
        {
            while (reader.Read())
            {
            }

            return null;
        }
        catch (XmlException failure)
        {
            return $"{relative}({failure.LineNumber},{failure.LinePosition}): {failure.Message}";
        }
    }

    /// <summary>Every file under a directory, skipping what is not ours.</summary>
    private static IEnumerable<FileInfo> Walk(DirectoryInfo directory)
    {
        foreach (var file in directory.EnumerateFiles())
        {
            yield return file;
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            if (NotOurs.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in Walk(child))
            {
                yield return file;
            }
        }
    }

    private static bool IsXml(FileInfo file) =>
        XmlExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase) ||
        DeclaresItselfXml(file);

    /// <summary>
    /// Whether the file opens with an XML declaration.
    /// </summary>
    /// <remarks>
    /// This is what covers the file somebody adds next year with an extension
    /// that is not in the list above. It reads the front of the file rather
    /// than all of it, and it is deliberately narrow: a document that says
    /// <c>&lt;?xml</c> is making a claim this can hold it to, while a file
    /// that merely begins with a <c>&lt;</c> might be HTML, a template, or
    /// prose.
    /// <para>
    /// A byte-order mark is skipped by the reader, and anything unreadable is
    /// not XML for this purpose -- a locked or vanished file is somebody
    /// else's problem and not a well-formedness one.
    /// </para>
    /// </remarks>
    private static bool DeclaresItselfXml(FileInfo file)
    {
        try
        {
            using var stream = file.OpenRead();
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            // Five characters and then whitespace: the declaration's version
            // is separated from it by any of it, not by a space in
            // particular, and "<?xmlfoo" is a processing instruction rather
            // than a declaration.
            var opening = new char[6];
            var read = reader.ReadBlock(opening, 0, opening.Length);

            return read == opening.Length &&
                   new string(opening, 0, 5) == "<?xml" &&
                   char.IsWhiteSpace(opening[5]);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
