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
    /// The most the cycle log may ever occupy, in megabytes.
    /// </summary>
    /// <remarks>
    /// A ceiling on the folder and not a window on the calendar, because the
    /// promise that has to be makeable to a ministry's system administrator is
    /// one sentence rather than an estimate. It is settable here -- which on
    /// Windows means the same <c>agent.ini</c> the ADL address lives in --
    /// for the machine whose disk is smaller than the fleet's, and for the
    /// support session that wants a deeper record for a week.
    /// <para>
    /// Independent of <see cref="GeneralLogMegabytes"/> on purpose: two
    /// ceilings mean a chatty subsystem can never evict cycle history.
    /// </para>
    /// </remarks>
    public int CycleLogMegabytes { get; set; } = Diagnostics.AgentLogs.CycleLogMegabytesDefault;

    /// <summary>The most the general log may ever occupy, in megabytes.</summary>
    public int GeneralLogMegabytes { get; set; } = Diagnostics.AgentLogs.GeneralLogMegabytesDefault;

    /// <summary>
    /// How much this machine writes to its general log.
    /// </summary>
    /// <remarks>
    /// <c>Information</c>, which is what a machine nobody is looking at should
    /// say. A support session asks for <c>Debug</c> for a day by editing one
    /// line of the settings file.
    /// <para>
    /// There is no auto-revert, and there does not need to be: the ceiling
    /// makes a machine left on <c>Debug</c> harmless -- it churns within its
    /// cap rather than filling a disk. What being left on it costs is
    /// retained window, which collapses from months to days.
    /// </para>
    /// <para>
    /// Letting ADL set this remotely is deliberately not here. A verbosity a
    /// machine can be told to change from the far end is part of the wire
    /// work, and this has to be usable on a machine that cannot reach ADL at
    /// all -- which is most of the machines anybody wants a log from.
    /// </para>
    /// </remarks>
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// Where state actually goes: what this says, or what the head decided.
    /// </summary>
    /// <remarks>
    /// Named once because three things now ask it -- the state store, the
    /// cycle log and the general log -- and a machine that had moved its
    /// state directory but whose logs stayed under the head's default would
    /// be a machine whose evidence is not where its operator was told to look.
    /// </remarks>
    public string ResolveStateDirectory(Platform.IHostLifecycle host) =>
        string.IsNullOrWhiteSpace(StateDirectory) ? host.StateDirectory : StateDirectory!;

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

    /// <summary>
    /// What is wrong with <paramref name="adlBaseUrl"/> as an address for a
    /// machine to report to, or <c>null</c> when nothing is.
    /// </summary>
    /// <remarks>
    /// The same question <see cref="DescribeConfigurationProblem"/> answers,
    /// asked about an address nothing is configured with yet. Two callers ask
    /// it before writing one: <c>adl-agent set-url</c>, so that a verb never
    /// writes a file the service will refuse to start from, and the tray's
    /// Change… dialog, so that nobody is asked for an administrator's password
    /// to write something that was never going to work.
    /// <para>
    /// Named here rather than left as a two-line expression at each of them.
    /// It was exactly that, twice, and the two copies are the one thing in
    /// this product that must never drift: a window that accepted what the
    /// verb refuses would raise a consent prompt for nothing, and a window
    /// that refused what the verb accepts would hide a usable address behind
    /// a sentence nobody could act on.
    /// </para>
    /// </remarks>
    public static string? ProblemWith(string adlBaseUrl) =>
        new AgentOptions { AdlBaseUrl = adlBaseUrl }.DescribeConfigurationProblem();

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
