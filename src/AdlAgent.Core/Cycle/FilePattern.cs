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
/// <c>[!a-z]</c> for a set, and everything else literal. Case is ignored,
/// which is <c>fnmatch</c>'s behaviour on the platform these agents run on
/// and the only behaviour a technician typing a pattern would expect of a
/// Windows folder.
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

    private static readonly Dictionary<string, FilePattern> Compiled =
        new(StringComparer.Ordinal);

    private static readonly Lock Gate = new();

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
    /// Cached across calls because the configuration is re-read every cycle
    /// and the patterns in it almost never change; compiling the same handful
    /// of globs every ten minutes for the life of a service is waste with no
    /// upside. Bounded by how many distinct patterns a device has ever been
    /// configured with, which is a number an administrator types by hand.
    /// </remarks>
    public static FilePattern For(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return MatchesNothing;
        }

        lock (Gate)
        {
            if (Compiled.TryGetValue(pattern, out var known))
            {
                return known;
            }

            var compiled = new FilePattern(
                pattern,
                new Regex(
                    Translate(pattern),
                    RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

            Compiled[pattern] = compiled;

            return compiled;
        }
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
