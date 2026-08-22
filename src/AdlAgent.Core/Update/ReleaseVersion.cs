using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AdlAgent.Core.Update;

/// <summary>
/// An agent version, as three numbers that can be put in order.
/// </summary>
/// <remarks>
/// Not <see cref="System.Version"/>, which is looser than this contract in a
/// way that matters: it reads "1.2" as a version whose build number is -1,
/// so <c>1.2 &lt; 1.2.0</c> -- and a fleet that downgraded itself every cycle
/// because two spellings of the same release did not compare equal is not a
/// bug anybody would find quickly. Three parts, all present, or it is not a
/// version this agent will act on.
/// <para>
/// The MSI product version is three fields too, which is not a coincidence:
/// Windows Installer only compares the first three, so a release scheme with
/// a fourth would produce upgrades Windows silently declines to perform.
/// </para>
/// <para>
/// A pre-release suffix (<c>0.2.0-rc1</c>) is read as the release it
/// qualifies. A developer's build calls itself that, and the only question
/// asked of the running version is whether what ADL offers is newer -- for
/// which "the 0.2.0 I am running" is the honest answer. ADL will not publish
/// a suffixed version, so this never decides an ordering between two of them.
/// </para>
/// </remarks>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch)
    : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? value, [NotNullWhen(true)] out ReleaseVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        // "0.2.0-rc1" and "0.2.0+abc123" are both this release.
        var suffix = text.IndexOfAny(['-', '+']);

        if (suffix >= 0)
        {
            text = text[..suffix];
        }

        var parts = text.Split('.');

        if (parts.Length != 3)
        {
            return false;
        }

        var numbers = new int[3];

        for (var index = 0; index < 3; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
            {
                return false;
            }
        }

        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2]);

        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var major = Major.CompareTo(other.Major);

        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);

        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
