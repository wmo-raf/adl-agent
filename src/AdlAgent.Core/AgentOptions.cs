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
    /// <remarks>
    /// Deliberately not <c>[Required]</c>. It was, and with
    /// <c>ValidateOnStart</c> above it that meant a machine installed without
    /// an address threw <c>OptionsValidationException</c> before the host
    /// ran -- on a service the MSI configures to restart on failure, which
    /// is a crash loop on a machine nobody can reach. A missing address is
    /// now a state the agent knows it is in and reports; see
    /// <see cref="DescribeConfigurationProblem"/>.
    /// </remarks>
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
    /// Whether this machine may replace itself with a newer agent.
    /// </summary>
    /// <remarks>
    /// On by default, which is decision #262's default and the only setting
    /// that makes sense for a fleet nobody can log into: the whole reason the
    /// feed is served by ADL is so that machines with no route to the
    /// internet still get fixes. The switch exists for the country whose IT
    /// department deploys software itself and would rather the agent did not,
    /// and for a machine somebody is debugging.
    /// <para>
    /// It is not the fleet-wide brake. Holding a machine back from the
    /// operator's chair is what the per-device version pin in the ADL admin
    /// is for (story 29); this is local, and a local setting cannot be
    /// changed by whoever is watching the fleet.
    /// </para>
    /// </remarks>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>
    /// The versioned agent surface, under ADL's plugin mount. Fixed: the
    /// mount point is part of the contract, and a machine pointed at a
    /// different one is misconfigured rather than differently configured.
    /// </summary>
    public const string ApiPath = "plugins/api/agent/v1/";

    /// <summary>
    /// What is wrong with <see cref="AdlBaseUrl"/>, or <c>null</c> when
    /// nothing is.
    /// </summary>
    /// <remarks>
    /// The same rules <see cref="ResolveApiBaseAddress"/> enforces, asked
    /// rather than thrown. Two callers need the answer without a machine
    /// falling over to give it: the loops, which have nowhere to send
    /// anything and skip their pass, and the status the tray draws, which
    /// has to tell "no address configured" apart from "an address that is
    /// not answering". They look the same on screen and they are fixed by
    /// different people.
    /// <para>
    /// One string rather than a flag, because every one of these states has
    /// a different sentence and the technician reading it is the person who
    /// has to act on it.
    /// </para>
    /// </remarks>
    public string? DescribeConfigurationProblem()
    {
        try
        {
            ResolveApiBaseAddress();

            return null;
        }
        catch (InvalidOperationException refusal)
        {
            return refusal.Message;
        }
    }

    /// <summary>True when this machine has somewhere to send to.</summary>
    public bool IsConfigured => DescribeConfigurationProblem() is null;

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
