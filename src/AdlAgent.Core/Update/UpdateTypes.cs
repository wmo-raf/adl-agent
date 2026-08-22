namespace AdlAgent.Core.Update;

/// <summary>
/// What ADL says this machine should be running.
/// </summary>
/// <remarks>
/// The whole answer is the server's, including the decision a pinned device
/// has already had made for it: an instance holding a newer release simply
/// does not offer it to a machine an operator has pinned, so the pin is not
/// something the agent could forget to honour (story 29). The agent's only
/// judgement is whether what it is offered is newer than what it is running.
/// </remarks>
public sealed record UpdateOffer
{
    /// <summary>
    /// The version this device should run, or <c>null</c> when the instance
    /// has nothing for it -- no published release, or a pin naming a release
    /// this instance does not hold.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>True when an operator has pinned this device (story 29).</summary>
    public bool Pinned { get; init; }

    /// <summary>
    /// ADL's own sentence about the offer, for the log and the tray.
    /// </summary>
    /// <remarks>
    /// Written server-side rather than assembled here, so that "you are
    /// pinned to 0.1.0" and "this instance holds no release yet" reach a
    /// technician as the two different situations they are, without the
    /// agent having to model either.
    /// </remarks>
    public string Reason { get; init; } = "";

    /// <summary>The package to fetch, or <c>null</c> when nothing is offered.</summary>
    public UpdateArtifact? Artifact { get; init; }
}

/// <summary>One installable package, and how to know it arrived intact.</summary>
public sealed record UpdateArtifact
{
    /// <summary>
    /// Which kind of package this is -- an MSI for the service tier, a
    /// Velopack release for the per-user tier. Chosen by ADL from the tier
    /// the agent asked as, not by the agent from a list.
    /// </summary>
    public string Kind { get; init; } = "";

    /// <summary>
    /// Where to fetch it, relative to the agent API's base address.
    /// </summary>
    /// <remarks>
    /// Relative on purpose. An absolute URL would let the answer to one
    /// request decide which host a country server downloads an executable
    /// from, and the entire premise of this product is that the machine
    /// talks to its own ADL instance and to nothing else. Keeping it
    /// relative makes that structural rather than a promise.
    /// </remarks>
    public string Path { get; init; } = "";

    /// <summary>What to call the file on disk. ADL's name for it, sanitised here.</summary>
    public string FileName { get; init; } = "";

    /// <summary>
    /// The SHA-256 an honest download has, lower-case hex.
    /// </summary>
    /// <remarks>
    /// Checked on every update regardless of code signing (decision #262):
    /// pilots ship unsigned, and this is the whole of what stands between a
    /// truncated or tampered download and the binary that runs as
    /// LocalSystem on a national meteorological service's server.
    /// </remarks>
    public string Sha256 { get; init; } = "";

    /// <summary>How many bytes it should be. A cheap first refusal.</summary>
    public long Size { get; init; }
}

/// <summary>
/// How an install describes itself when asking what it should be running.
/// </summary>
/// <remarks>
/// The two tiers decision #262 ships: an MSI-installed Windows Service for
/// machines whose technician has administrator rights, and a per-user
/// Velopack install for machines whose technician does not (story 3). They
/// take different packages, so the tier travels with the question rather
/// than being configured on either side.
/// </remarks>
public static class UpdateTiers
{
    public const string Service = "service";
    public const string User = "user";
}

/// <summary>
/// The kinds of package the feed serves, and what they are called on disk.
/// </summary>
/// <remarks>
/// Protocol vocabulary rather than platform knowledge: these are ADL's
/// spellings, and the core needs them only to give a downloaded file a name
/// the platform's installer will recognise -- Windows Installer will not
/// look at a file that is not called <c>.msi</c>, however good its bytes
/// are. What to *do* with each is the head's, behind
/// <see cref="Platform.IUpdateInstaller"/>.
/// </remarks>
public static class UpdateKinds
{
    /// <summary>A Windows Installer package: the service tier's upgrade.</summary>
    public const string Msi = "msi";

    /// <summary>A Velopack full release: the per-user tier's upgrade.</summary>
    public const string VelopackFull = "velopack_full";

    public static string ExtensionFor(string? kind) => kind switch
    {
        Msi => ".msi",
        VelopackFull => ".nupkg",

        // A kind this agent has never heard of, from an ADL newer than it.
        // Named so that whoever finds it on disk can see what it was, and
        // left for the head to refuse.
        _ => ".pkg",
    };
}

/// <summary>A verified package, waiting on disk for the head to install it.</summary>
/// <param name="Version">What this package installs.</param>
/// <param name="Kind">The kind ADL served, which decides how it is applied.</param>
/// <param name="Path">The file on this machine, already hash-verified.</param>
public sealed record DownloadedUpdate(string Version, string Kind, string Path);
