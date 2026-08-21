using System.Text;
using System.Text.RegularExpressions;

namespace AdlAgent.Core.Cycle;

/// <summary>
/// The glob a station's filenames must match, compiled once.
/// </summary>
/// <remarks>
/// The pattern is the whole of how one folder's entries are divided between
/// the stations that share it, so what it means here has to be what it means
/// in ADL. ADL matches with Python's <c>fnmatch</c>, and this follows it:
/// <c>*</c> for any run of characters, <c>?</c> for one, <c>[abc]</c> and
/// <c>[!a-z]</c> for a set, and everything else literal.
/// <para>
/// Case is ignored, and that is a decision rather than an oversight.
/// <c>fnmatch</c> folds case through <c>os.path.normcase</c>, so ADL's own
/// match count -- the number a technician checks their pattern against in the
/// admin -- ignores case on the Windows servers this ships to. Matching
/// case-sensitively here would make the agent disagree with the count the
/// person was shown. A Linux head has to revisit this: there
/// <c>GARISSA.dat</c> and <c>garissa.dat</c> are two files, and folding them
/// together could route one station's observations to another's ledger.
/// </para>
/// <para>
/// Compiled to a regular expression rather than matched character by
/// character because the whole point of the enumerate strategy is that a
/// folder is walked once and every entry is tested against every pattern in
/// it: on a hundred-thousand-file folder that is the inner loop.
/// </para>
/// </remarks>
public sealed class FilePattern
{
    /// <summary>
    /// A pattern that matches nothing, which is what a station link with no
    /// pattern configured has.
    /// </summary>
    /// <remarks>
    /// Nothing rather than everything, deliberately. ADL requires a pattern
    /// for an enumerating link, so a blank one is a configuration that never
    /// should have been saved -- and the folder it names is often shared.
    /// Guessing "everything" there would ship one station's files to another
    /// station's ledger, which is a wrong number in a national archive; the
    /// cycle reports the misconfiguration instead.
    /// </remarks>
    public static readonly FilePattern MatchesNothing = new("", null);

    private readonly Regex? _matcher;

    private FilePattern(string text, Regex? matcher)
    {
        Text = text;
        _matcher = matcher;
    }

    /// <summary>The glob as it was configured, for messages a person reads.</summary>
    public string Text { get; }

    /// <summary>True when this pattern can never match anything.</summary>
    public bool IsEmpty => _matcher is null;

    /// <summary>
    /// The compiled form of <paramref name="pattern"/>.
    /// </summary>
    /// <remarks>
    /// Compiling costs real time, so a caller with several links to serve
    /// should compile each distinct pattern once and keep it for the length
    /// of the scan -- see <see cref="FolderScanner"/>. Nothing is cached
    /// here: a static cache would outlive every pattern a device was ever
    /// configured with, on a service that runs for months, to save a
    /// millisecond every ten minutes.
    /// </remarks>
    public static FilePattern For(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return MatchesNothing;
        }

        return new FilePattern(
            pattern,
            new Regex(
                Translate(pattern),
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    /// <summary>True when a file of this name belongs to the station.</summary>
    public bool Matches(string name) => _matcher is not null && _matcher.IsMatch(name);

    /// <summary>The glob as a regular expression anchored to the whole name.</summary>
    private static string Translate(string pattern)
    {
        var expression = new StringBuilder("^");

        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];

            switch (character)
            {
                case '*':
                    expression.Append(".*");
                    break;

                case '?':
                    expression.Append('.');
                    break;

                case '[':
                    index = AppendSet(pattern, index, expression);
                    break;

                default:
                    expression.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        return expression.Append('$').ToString();
    }

    /// <summary>
    /// Copy a <c>[...]</c> set across, returning the index of its close.
    /// </summary>
    /// <remarks>
    /// A set that is never closed is not a set: <c>fnmatch</c> treats the
    /// bracket as a literal in that case, and a technician who typed one by
    /// accident should get a pattern that matches their file rather than one
    /// that throws.
    /// </remarks>
    private static int AppendSet(string pattern, int open, StringBuilder expression)
    {
        var close = pattern.IndexOf(']', open + 1);

        // A ']' immediately after the opening bracket (or its negation) is a
        // literal ']', exactly as in a regular expression.
        if (close == open + 1 || (close == open + 2 && pattern[open + 1] is '!' or '^'))
        {
            close = pattern.IndexOf(']', close + 1);
        }

        if (close < 0)
        {
            expression.Append("\\[");

            return open;
        }

        var body = pattern[(open + 1)..close];

        expression.Append('[');
        expression.Append(body.StartsWith('!') ? "^" + body[1..] : body);
        expression.Append(']');

        return close;
    }
}
