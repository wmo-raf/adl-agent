namespace AdlAgent.Core.Tests;

/// <summary>
/// That the Windows Installer condition reader reads what Windows would.
/// </summary>
/// <remarks>
/// It exists to check a rule that is written twice (see
/// <see cref="MsiCondition"/>), so it is itself a third copy of something, and
/// a third copy nobody checked would just move the place the answer is wrong.
/// These are the operators and the precedence the installer's screen relies
/// on, stated against Windows Installer's own documented meanings.
/// </remarks>
public class MsiConditionTests
{
    [Theory]
    [InlineData("P", "anything", true)]
    [InlineData("P", "", false)]
    [InlineData("NOT P", "", true)]
    [InlineData("NOT P", "anything", false)]
    public void A_property_on_its_own_is_true_when_it_is_set(string condition, string value, bool expected) =>
        Assert.Equal(expected, MsiCondition.Evaluate(condition, "P", value));

    [Theory]
    [InlineData("P = \"abc\"", "abc", true)]
    [InlineData("P = \"abc\"", "ABC", false)]
    [InlineData("P ~= \"abc\"", "ABC", true)]
    [InlineData("P <> \"abc\"", "abd", true)]
    [InlineData("P << \"ab\"", "abc", true)]
    [InlineData("P << \"bc\"", "abc", false)]
    [InlineData("P ~<< \"AB\"", "abc", true)]
    [InlineData("P >> \"bc\"", "abc", true)]
    [InlineData("P >< \"b\"", "abc", true)]
    [InlineData("P >< \" \"", "a b", true)]
    [InlineData("P >< \" \"", "ab", false)]
    public void Strings_compare_the_way_Windows_Installer_compares_them(
        string condition, string value, bool expected) =>
        Assert.Equal(expected, MsiCondition.Evaluate(condition, "P", value));

    [Theory]
    // NOT binds tighter than AND, which binds tighter than OR.
    [InlineData("NOT P << \"x\"", "abc", true)]
    [InlineData("P AND P << \"a\"", "abc", true)]
    [InlineData("P << \"z\" OR P << \"a\" AND P >> \"c\"", "abc", true)]
    [InlineData("(P << \"z\" OR P << \"a\") AND P >> \"z\"", "abc", false)]
    [InlineData("NOT (P << \"a\") OR P >> \"c\"", "abc", true)]
    public void Precedence_is_comparison_then_NOT_then_AND_then_OR(
        string condition, string value, bool expected) =>
        Assert.Equal(expected, MsiCondition.Evaluate(condition, "P", value));

    /// <summary>
    /// A property nobody set reads as empty, which is how a machine passed no
    /// <c>ADLURL</c> at all is seen -- the case every silent self-update is.
    /// </summary>
    [Fact]
    public void A_property_that_was_never_set_is_empty()
    {
        var nothing = new Dictionary<string, string>();

        Assert.False(MsiCondition.Evaluate("ADLURL", nothing));
        Assert.True(MsiCondition.Evaluate("NOT ADLURL", nothing));
    }

    /// <summary>
    /// Anything outside the subset is refused rather than guessed at.
    /// </summary>
    /// <remarks>
    /// The point of the whole class is to be trusted about a string WiX never
    /// looks at. A reader that quietly returned <c>false</c> for a condition
    /// it could not parse would turn every test that uses it into one that
    /// passes for the wrong reason.
    /// </remarks>
    [Theory]
    [InlineData("P >= \"1\"")]
    [InlineData("P AND")]
    [InlineData("(P")]
    [InlineData("P \"abc\"")]
    [InlineData("P << \"abc")]
    public void A_condition_it_cannot_read_is_an_error(string condition) =>
        Assert.Throws<FormatException>(() => MsiCondition.Evaluate(condition, "P", "abc"));
}
