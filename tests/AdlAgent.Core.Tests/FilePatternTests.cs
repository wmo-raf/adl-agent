using AdlAgent.Core.Cycle;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The glob that divides one folder's files between the stations that share
/// it -- and therefore has to mean here what it means in ADL.
/// </summary>
/// <remarks>
/// ADL matches with Python's <c>fnmatch</c>. A pattern that means one thing
/// in the admin, where it was typed and its match count was checked, and
/// another on the machine would file one station's observations under
/// another's name, and nothing downstream would ever question it.
/// </remarks>
public class FilePatternTests
{
    [Theory]
    [InlineData("GARISSA_*.dat", "GARISSA_20260821.dat", true)]
    [InlineData("GARISSA_*.dat", "MOMBASA_20260821.dat", false)]
    [InlineData("GARISSA_*.dat", "GARISSA_20260821.dat.tmp", false)]
    [InlineData("*", "anything at all", true)]
    [InlineData("*.dat", "no-extension", false)]
    [InlineData("st??.dat", "st01.dat", true)]
    [InlineData("st??.dat", "st001.dat", false)]
    [InlineData("st[0-9].dat", "st7.dat", true)]
    [InlineData("st[0-9].dat", "stx.dat", false)]
    [InlineData("st[!0-9].dat", "stx.dat", true)]
    [InlineData("st[!0-9].dat", "st7.dat", false)]
    // A pattern with a bracket nobody closed is a technician's typo, not a
    // pattern: fnmatch reads the bracket literally, and so does this.
    [InlineData("data[.dat", "data[.dat", true)]
    // Windows folders do not distinguish case, and neither does the admin's
    // own match count.
    [InlineData("GARISSA_*.DAT", "garissa_20260821.dat", true)]
    // Regular-expression syntax in a filename is a filename.
    [InlineData("a+b.dat", "a+b.dat", true)]
    [InlineData("a+b.dat", "aab.dat", false)]
    public void A_pattern_claims_the_names_ADL_would_claim(
        string pattern, string name, bool matches)
    {
        Assert.Equal(matches, FilePattern.For(pattern).Matches(name));
    }

    [Fact]
    public void A_station_with_no_pattern_claims_nothing()
    {
        // Never "everything": the folder is nearly always shared, and a
        // station that claimed all of it would put its neighbours' data in
        // its own ledger rows.
        foreach (var blank in new[] { null, "", "   " })
        {
            var pattern = FilePattern.For(blank);

            Assert.True(pattern.IsEmpty);
            Assert.False(pattern.Matches("GARISSA_20260821.dat"));
        }
    }
}
