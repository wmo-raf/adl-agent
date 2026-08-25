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
    /// deployment leaves behind. All of it is in <c>.gitignore</c>, and all
    /// of it can hold copies of files that are checked here at their source —
    /// <c>publish/</c> and <c>.dev-publish/</c> both carry the tray's
    /// manifest — so a walk that did not skip them would report the same
    /// problem several times, and would fail on one developer's machine over
    /// something no other machine has.
    /// </remarks>
    private static readonly string[] NotOurs =
    [
        ".git", ".idea", ".vs", ".vscode", ".dev-publish",
        "bin", "obj", "publish", "artifacts", "TestResults", "node_modules",
    ];

    /// <summary>
    /// What is XML by its name.
    /// </summary>
    /// <remarks>
    /// Two ways in, because neither alone finds everything. This list is for
    /// the documents that carry no declaration: an SDK-style <c>.csproj</c>
    /// opens on <c>&lt;Project&gt;</c>, a <c>.slnx</c> on
    /// <c>&lt;Solution&gt;</c>, and every <c>.xaml</c> in the tray on its
    /// root element. The other way in is <see cref="DeclaresItselfXml"/>,
    /// which is what covers a file with an extension nobody thought of.
    /// </remarks>
    private static readonly string[] XmlExtensions =
    [
        ".xml", ".xaml", ".manifest", ".config", ".svg",
        ".csproj", ".props", ".targets", ".slnx", ".nuspec",
        ".wxs", ".wxi", ".wxl", ".resx", ".xsd", ".xsl", ".xslt",
        ".vsixmanifest", ".ruleset",
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
    /// Why a parser will not take this document, or <c>null</c> when it will.
    /// </summary>
    /// <remarks>
    /// <see cref="XmlReader"/> rather than a search for the mistake that has
    /// already been made once: the rule being asserted is "well-formed", and
    /// enumerating the ways a document can fail to be is how you miss the
    /// next one.
    /// <para>
    /// The message is in the shape a compiler uses — <c>path(line,column):
    /// reason</c> — because it is read in the same places, and because a
    /// failure that names the line is the difference between an edit and a
    /// search.
    /// </para>
    /// </remarks>
    public static string? WhyNotWellFormed(string path, string? displayPath = null)
    {
        using var reader = XmlReader.Create(path);

        try
        {
            while (reader.Read())
            {
            }

            return null;
        }
        catch (XmlException failure)
        {
            return $"{displayPath ?? path}({failure.LineNumber},{failure.LinePosition}): {failure.Message}";
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
    /// not XML for this purpose — a locked or vanished file is somebody
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
