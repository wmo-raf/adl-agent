using System.Xml;
using System.Xml.Linq;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The installer's sources, read the way the WiX compiler reads them.
/// </summary>
/// <remarks>
/// Not a general-purpose reader. The one thing it does that
/// <see cref="XDocument"/> does not is expand the
/// <c>&lt;?define?&gt;</c> variables the conditions are written as, which the
/// WiX preprocessor does before the compiler ever sees an attribute. They are
/// written that way because the same rule appears on four attributes of one
/// dialog and four copies of it would be four chances to change three; a test
/// that read them unexpanded would be checking a variable name.
/// </remarks>
internal static class WixSource
{
    public static readonly XNamespace Wxs = "http://wixtoolset.org/schemas/v4/wxs";

    /// <summary>One of the installer's source documents, by file name.</summary>
    public static XDocument Load(string file) =>
        XDocument.Load(Path.Combine(MsiDirectory, file));

    /// <summary>Where the installer's sources are.</summary>
    public static string MsiDirectory => Path.Combine(PackagingDirectory, "msi");

    /// <summary>Where the packaging scripts are.</summary>
    public static string PackagingDirectory => Path.Combine(RepositoryRoot.Path, "packaging");

    /// <summary>
    /// One condition off an element, as the WiX preprocessor would leave it.
    /// </summary>
    public static string Condition(this XElement element, string attribute)
    {
        var condition = (string?)element.Attribute(attribute)
            ?? throw new XmlException(
                $"<{element.Name.LocalName} Id=\"{(string?)element.Attribute("Id")}\"> has no {attribute}.");

        var definitions = element.Document!.DescendantNodes()
            .OfType<XProcessingInstruction>()
            .Where(instruction => instruction.Target == "define")
            .Select(instruction => instruction.Data.Split('=', 2))
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());

        // Definitions may be written in terms of earlier ones, so this repeats
        // until nothing is left to replace -- and refuses to loop for ever
        // over a name that does not resolve.
        for (var pass = 0; condition.Contains("$("); pass++)
        {
            if (pass > definitions.Count)
            {
                throw new XmlException($"A preprocessor variable in '{condition}' never resolves.");
            }

            foreach (var (name, value) in definitions)
            {
                condition = condition
                    .Replace($"$(var.{name})", value)
                    .Replace($"$({name})", value);
            }
        }

        return condition;
    }
}
