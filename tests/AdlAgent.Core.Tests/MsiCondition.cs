namespace AdlAgent.Core.Tests;

/// <summary>
/// A Windows Installer conditional expression, evaluated here.
/// </summary>
/// <remarks>
/// The installer's one screen refuses an address the agent would refuse, and
/// it has to say so in Windows Installer's condition language rather than in
/// C# -- there is no code of ours running while somebody types into an MSI
/// dialog. So the rule exists twice, and the only thing that can keep the two
/// copies honest is to run them against the same addresses and compare. This
/// is what runs the installer's copy; see <see cref="InstallerDialogTests"/>.
/// <para>
/// A subset, and deliberately a small one: the operators the dialog actually
/// uses. Anything else throws rather than guessing, because a condition this
/// silently mis-read would be a test that passes while the installer does
/// something else -- which is worse than no test. WiX itself is no help here:
/// it stores a condition as an opaque string and never parses it, so a
/// malformed one compiles, links, ships, and is first evaluated on a country
/// server.
/// </para>
/// <para>
/// The semantics are Windows Installer's, from the Conditional Statement
/// Syntax reference: an identifier is the value of that property (empty when
/// it is not set), a bare value is true when it is not empty, comparisons
/// against a literal are string comparisons, <c>&lt;&lt;</c> is "starts
/// with", <c>&gt;&gt;</c> is "ends with", <c>&gt;&lt;</c> is "contains", and
/// a leading <c>~</c> makes any of them case-insensitive. Precedence is
/// comparison, then NOT, then AND, then OR.
/// </para>
/// <para>
/// Which leaves one thing this cannot rule out: that Windows Installer does
/// something other than its own documentation says, and this agrees with the
/// documentation rather than with Windows. Nothing that runs on a Linux CI
/// runner can close that, and what does is
/// <c>packaging/verify-msi-install.ps1</c> -- it installs the built package on
/// Windows and reads the file it wrote.
/// </para>
/// </remarks>
internal static class MsiCondition
{
    /// <summary>Evaluate <paramref name="condition"/> against <paramref name="properties"/>.</summary>
    /// <exception cref="FormatException">
    /// The condition uses something outside the subset this understands.
    /// </exception>
    public static bool Evaluate(string condition, IReadOnlyDictionary<string, string> properties)
    {
        var reader = new Reader(Tokenize(condition), properties);
        var value = reader.ReadExpression();

        reader.ExpectEnd();

        return value;
    }

    /// <summary>Evaluate a condition over a single property.</summary>
    public static bool Evaluate(string condition, string property, string value) =>
        Evaluate(condition, new Dictionary<string, string> { [property] = value });

    private enum Kind
    {
        Identifier,
        Literal,
        Comparison,
        And,
        Or,
        Not,
        Open,
        Close,
        End,
    }

    private readonly record struct Token(Kind Kind, string Text);

    /// <summary>
    /// The operators this understands, longest first so that <c>&lt;&gt;</c>
    /// is never read as <c>&lt;</c> followed by something else.
    /// </summary>
    private static readonly string[] Comparisons = ["<>", "<<", ">>", "><", "="];

    private static List<Token> Tokenize(string condition)
    {
        var tokens = new List<Token>();
        var index = 0;

        while (index < condition.Length)
        {
            var character = condition[index];

            if (char.IsWhiteSpace(character))
            {
                index++;

                continue;
            }

            if (character == '(' || character == ')')
            {
                tokens.Add(new Token(character == '(' ? Kind.Open : Kind.Close, character.ToString()));
                index++;

                continue;
            }

            if (character == '"')
            {
                var close = condition.IndexOf('"', index + 1);

                if (close < 0)
                {
                    throw new FormatException($"Unterminated string literal in condition: {condition}");
                }

                tokens.Add(new Token(Kind.Literal, condition[(index + 1)..close]));
                index = close + 1;

                continue;
            }

            if (character == '~' || Comparisons.Any(o => Match(condition, index, o)))
            {
                var insensitive = character == '~';
                var start = insensitive ? index + 1 : index;
                var operation = Comparisons.FirstOrDefault(o => Match(condition, start, o))
                    ?? throw new FormatException(
                        $"Condition uses an operator this does not understand, at '{condition[index..]}'.");

                tokens.Add(new Token(Kind.Comparison, (insensitive ? "~" : "") + operation));
                index = start + operation.Length;

                continue;
            }

            if (char.IsLetter(character) || character == '_')
            {
                var end = index;

                while (end < condition.Length &&
                       (char.IsLetterOrDigit(condition[end]) || condition[end] is '_' or '.'))
                {
                    end++;
                }

                var word = condition[index..end];

                tokens.Add(word.ToUpperInvariant() switch
                {
                    "AND" => new Token(Kind.And, word),
                    "OR" => new Token(Kind.Or, word),
                    "NOT" => new Token(Kind.Not, word),
                    _ => new Token(Kind.Identifier, word),
                });

                index = end;

                continue;
            }

            throw new FormatException(
                $"Condition uses something this does not understand, at '{condition[index..]}'.");
        }

        tokens.Add(new Token(Kind.End, ""));

        return tokens;
    }

    private static bool Match(string condition, int index, string operation) =>
        index + operation.Length <= condition.Length &&
        condition.AsSpan(index, operation.Length).SequenceEqual(operation);

    private sealed class Reader(List<Token> tokens, IReadOnlyDictionary<string, string> properties)
    {
        private int _position;

        private Token Current => tokens[_position];

        public bool ReadExpression()
        {
            var value = ReadTerm();

            while (Current.Kind == Kind.Or)
            {
                _position++;

                // Not short-circuited: a malformed right-hand side should be
                // reported however the left-hand side came out, so that a
                // condition this cannot read never passes quietly.
                value = ReadTerm() || value;
            }

            return value;
        }

        public void ExpectEnd()
        {
            if (Current.Kind != Kind.End)
            {
                throw new FormatException($"Unexpected '{Current.Text}' at the end of a condition.");
            }
        }

        private bool ReadTerm()
        {
            var value = ReadFactor();

            while (Current.Kind == Kind.And)
            {
                _position++;

                // Read first, combine second, for the reason ReadExpression
                // gives: a right-hand side this cannot read must be an error
                // however the left-hand side came out.
                value = ReadFactor() && value;
            }

            return value;
        }

        private bool ReadFactor()
        {
            if (Current.Kind == Kind.Not)
            {
                _position++;

                return !ReadFactor();
            }

            if (Current.Kind == Kind.Open)
            {
                _position++;

                var value = ReadExpression();

                if (Current.Kind != Kind.Close)
                {
                    throw new FormatException("A '(' in a condition is never closed.");
                }

                _position++;

                return value;
            }

            return ReadComparison();
        }

        private bool ReadComparison()
        {
            var left = ReadOperand();

            if (Current.Kind != Kind.Comparison)
            {
                // A value on its own: true when it is not empty.
                return left.Length > 0;
            }

            var operation = Current.Text;

            _position++;

            var right = ReadOperand();
            var comparison = operation.StartsWith('~')
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return operation.TrimStart('~') switch
            {
                "=" => left.Equals(right, comparison),
                "<>" => !left.Equals(right, comparison),
                "<<" => left.StartsWith(right, comparison),
                ">>" => left.EndsWith(right, comparison),
                "><" => left.Contains(right, comparison),
                _ => throw new FormatException($"Condition uses the operator '{operation}'."),
            };
        }

        private string ReadOperand()
        {
            var token = Current;

            _position++;

            return token.Kind switch
            {
                Kind.Literal => token.Text,

                // An unset property is the empty string, which is how Windows
                // Installer reads one: a machine that was passed no ADLURL and
                // one that was passed an empty ADLURL are the same machine.
                Kind.Identifier => properties.GetValueOrDefault(token.Text, ""),

                _ => throw new FormatException($"Expected a property or a string, found '{token.Text}'."),
            };
        }
    }
}
