using System.Reflection;

namespace AdlAgent.Core;

/// <summary>
/// What this agent calls itself.
/// </summary>
/// <remarks>
/// Read from the assembly rather than kept in a constant, so the number in
/// the fleet listing is the number the build produced. It travels on every
/// call in <c>X-Agent-Version</c> -- not only on heartbeats -- so that an
/// instance knows what a machine is running from its very first request.
/// </remarks>
public static class AgentVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(AgentVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(AgentVersion).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        // Strip the source-revision suffix the SDK appends ("1.2.3+abc123"):
        // the commit is in the build, not in something a person reads off a
        // fleet listing.
        var plus = informational.IndexOf('+');

        return plus < 0 ? informational : informational[..plus];
    }
}
