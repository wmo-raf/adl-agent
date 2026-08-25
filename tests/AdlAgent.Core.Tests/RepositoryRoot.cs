namespace AdlAgent.Core.Tests;

/// <summary>
/// Where this repository begins, found from wherever the test binary is.
/// </summary>
/// <remarks>
/// Several tests read files that are sources rather than build output — the
/// installer's <c>.wxs</c>, the packaging scripts, every XML the repository
/// ships — and none of them are copied next to the assembly. Each used to
/// walk up from <see cref="AppContext.BaseDirectory"/> looking for its own
/// landmark, which is the same loop written once per thing being looked for.
/// <para>
/// The landmark is the solution file: it is tracked, it is at the top, and
/// there is exactly one of it. A directory would do as well until somebody
/// nests one.
/// </para>
/// </remarks>
internal static class RepositoryRoot
{
    private const string Landmark = "AdlAgent.slnx";

    /// <summary>The directory holding the solution file.</summary>
    /// <exception cref="DirectoryNotFoundException">
    /// The test binary is somewhere with no repository above it.
    /// </exception>
    public static string Path
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null)
            {
                if (File.Exists(System.IO.Path.Combine(directory.FullName, Landmark)))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                $"Could not find {Landmark} above {AppContext.BaseDirectory}. " +
                "These tests read the repository's own sources.");
        }
    }
}
