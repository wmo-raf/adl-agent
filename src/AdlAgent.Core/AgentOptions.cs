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
    /// The versioned agent surface, under ADL's plugin mount. Configurable
    /// only so that a future contract version does not need a new release to
    /// be pointed at.
    /// </summary>
    public string ApiPath { get; set; } = "plugins/api/agent/v1/";

    /// <summary>The base address every call is made against.</summary>
    public Uri ResolveApiBaseAddress()
    {
        if (string.IsNullOrWhiteSpace(AdlBaseUrl))
        {
            throw new InvalidOperationException(
                "No ADL URL is configured. Set Agent:AdlBaseUrl to the address of the ADL instance this machine sends to.");
        }

        var root = AdlBaseUrl.TrimEnd('/') + "/";

        return new Uri(new Uri(root), ApiPath.TrimStart('/'));
    }
}
