using System.ComponentModel.DataAnnotations;

namespace AdlAgent.Core;

/// <summary>
/// The only settings that live on the machine.
/// </summary>
/// <remarks>
/// Everything else about how this agent behaves comes from ADL and is
/// re-read every cycle (decision #260). What cannot come from ADL is how to
/// reach ADL, so that is what is here -- plus, for a head that wants to
/// override it, where state is written.
/// </remarks>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>
    /// Root URL of the ADL instance this machine pairs with, e.g.
    /// <c>https://adl.example.org</c>. One agent, one instance.
    /// </summary>
    [Required]
    public string AdlBaseUrl { get; set; } = "";

    /// <summary>
    /// Where the token and the configuration cache are written. Left unset
    /// the head decides, which is what should normally happen -- see
    /// <see cref="Platform.IHostLifecycle.StateDirectory"/>.
    /// </summary>
    public string? StateDirectory { get; set; }

    /// <summary>
    /// How long any one call to ADL may take. Generous, because the links
    /// these machines sit on are slow, and bounded, because a request that
    /// hangs forever silently stops the loop it was made from.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The versioned agent surface, under ADL's plugin mount. Fixed: the
    /// mount point is part of the contract, and a machine pointed at a
    /// different one is misconfigured rather than differently configured.
    /// </summary>
    public const string ApiPath = "plugins/api/agent/v1/";

    /// <summary>The base address every call is made against.</summary>
    /// <exception cref="InvalidOperationException">
    /// The configured URL is missing, unparseable, or plain HTTP to somewhere
    /// other than this machine.
    /// </exception>
    public Uri ResolveApiBaseAddress()
    {
        if (string.IsNullOrWhiteSpace(AdlBaseUrl))
        {
            throw new InvalidOperationException(
                "No ADL URL is configured. Set Agent:AdlBaseUrl to the address of the ADL instance this machine sends to.");
        }

        if (!Uri.TryCreate(AdlBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var root))
        {
            throw new InvalidOperationException(
                $"Agent:AdlBaseUrl is not a URL: '{AdlBaseUrl}'.");
        }

        if (root.Scheme != Uri.UriSchemeHttps && !root.IsLoopback)
        {
            // The whole product is one outbound HTTPS call carrying a bearer
            // token and a country's observations. Refusing plain HTTP here is
            // cheaper than discovering a fleet was configured without it.
            // Loopback stays allowed, because that is a test fixture, not a
            // network.
            throw new InvalidOperationException(
                $"Agent:AdlBaseUrl must be https, not '{root.Scheme}'. The device token travels on every call.");
        }

        return new Uri(root, ApiPath);
    }
}
