using System.Text.Json.Serialization;

namespace AdlAgent.Core.Api;

/// <summary>What ADL says about this machine. Never carries a credential.</summary>
public sealed record DeviceSummary
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public DateTimeOffset? PairedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
}

/// <summary>The answer to a pairing code: the token, and who ADL thinks you are.</summary>
public sealed record PairResponse
{
    public string Token { get; init; } = "";
    public DeviceSummary Device { get; init; } = new();
}

/// <summary>
/// Everything this device needs for a cycle, as it arrived from ADL.
/// </summary>
/// <remarks>
/// This is also exactly what gets written to the offline cache, byte for
/// byte: a cached configuration and a fresh one are the same shape, so no
/// code downstream has to ask which it is holding.
/// </remarks>
public sealed record SyncResponse
{
    public long ConfigVersion { get; init; }
    public AgentLimits Limits { get; init; } = new();
    public DeviceConfig Device { get; init; } = new();
    public IReadOnlyList<ConnectionConfig> Connections { get; init; } = [];
}

/// <summary>What one call may carry. Handed out by ADL, never compiled in.</summary>
public sealed record AgentLimits
{
    public int ManifestEntries { get; init; } = 500;
    public long FileBytes { get; init; } = 50 * 1024 * 1024;

    /// <summary>
    /// How many files this machine may have in flight at once, across every
    /// station it serves.
    /// </summary>
    /// <remarks>
    /// ADL's to set, because the scarce thing is the country's link and the
    /// capacity of the instance at the other end of it -- neither of which a
    /// machine in a vendor's server room can see. Four is what an ADL that
    /// predates the field is taken to mean, which is the same reading of
    /// silence the reconciliation interval and the dated-folder window get.
    /// </remarks>
    public int ConcurrentUploads { get; init; } = 4;
}

public sealed record DeviceConfig
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public DateTimeOffset? PairedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>How often the scan cycle runs. Per device, not per connection.</summary>
    public int CheckIntervalMinutes { get; init; } = 10;

    /// <summary>
    /// How often to heartbeat -- a different cadence for a different loop,
    /// deliberately not derived from the check interval.
    /// </summary>
    public int HeartbeatIntervalMinutes { get; init; } = 5;

    /// <summary>
    /// How often each enumerating station offers its whole folder rather than
    /// only what the candidate window admits. Zero or less switches sweeps
    /// off.
    /// </summary>
    /// <remarks>
    /// Nullable because an ADL that predates the setting sends nothing, and
    /// the daily default the spec asks for is the right reading of silence.
    /// A zero would be indistinguishable from that if this were an
    /// <see cref="int"/>, and "the field is absent" and "the administrator
    /// turned it off" are opposite instructions.
    /// </remarks>
    public int? ReconciliationIntervalHours { get; init; }

    /// <summary>
    /// How far back an ordinary cycle walks the dated sub-folders of a
    /// station filed by date. Zero or less is the current folder alone.
    /// </summary>
    /// <remarks>
    /// The bound on the one thing about a dated tree that is not free.
    /// Expanding from a station's collection start date to now is 8,760
    /// directories for a year at hour granularity, and an ordinary cycle
    /// would enumerate every one of them every ten minutes for a station
    /// whose files are all in the newest one or two -- so a routine cycle
    /// takes this window and the reconciliation sweep takes the rest, once a
    /// day.
    /// <para>
    /// Nullable for the same reason
    /// <see cref="ReconciliationIntervalHours"/> is: an ADL that predates the
    /// setting sends nothing, and <see cref="Cycle.DatedFolders.DefaultRecentWindow"/>
    /// is the right reading of silence. A zero would be indistinguishable
    /// from that if this were an <see cref="int"/>, and "the field is absent"
    /// and "the administrator asked for today's folder only" are different
    /// instructions.
    /// </para>
    /// </remarks>
    public int? DatedFolderWindowHours { get; init; }
}

public sealed record ConnectionConfig
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public ConnectionAdminConfig Admin { get; init; } = new();
    public IReadOnlyList<StationLinkConfig> StationLinks { get; init; } = [];
}

public sealed record ConnectionAdminConfig
{
    public bool Enabled { get; init; } = true;
    public string Network { get; init; } = "";

    /// <summary>
    /// How long one of this vendor's stations may say nothing before this
    /// machine calls it quiet.
    /// </summary>
    /// <remarks>
    /// Per connection because a cadence belongs to the vendor's software and
    /// not to the station it happens to be writing for: a device serving two
    /// vendors and forty stations has two cadences, not forty.
    /// <para>
    /// Already resolved when it arrives -- ADL folds its own default in
    /// before sending, so a deployment that changes that default is followed
    /// by the whole fleet on the next cycle. Nullable only for an ADL that
    /// predates the field, where <see cref="StationFlow"/>'s own fallback
    /// stands in.
    /// </para>
    /// </remarks>
    public int? StaleAfterMinutes { get; init; }
}

public sealed record StationLinkConfig
{
    public long Id { get; init; }

    /// <summary>The oldest file worth offering for this station. A floor.</summary>
    public DateTimeOffset? Watermark { get; init; }

    /// <summary>
    /// When ADL last received anything for this station, or null if it never
    /// has.
    /// </summary>
    /// <remarks>
    /// ADL's record rather than this machine's, and that is the whole reason
    /// it is on the wire: the agent keeps no history of what it delivered, so
    /// after a restart its own memory of every station is empty while this is
    /// not. It is what the station list judges a row by.
    /// <para>
    /// Every file ADL received counts toward it, whatever ADL then made of
    /// it. A file that failed to decode still proves the folder, the pattern,
    /// the share and the upload all worked, and that fault is fixed in the
    /// ADL admin rather than by anybody standing at this machine.
    /// </para>
    /// </remarks>
    public DateTimeOffset? LastReceivedAt { get; init; }

    /// <summary>The tier this machine may write. Exactly what the config endpoint accepts.</summary>
    public StationLinkAppConfig Config { get; init; } = new();

    /// <summary>HQ's tier. Travels so the app can show it; never written from here.</summary>
    public StationLinkAdminConfig Admin { get; init; } = new();
}

/// <summary>
/// Where this station's files sit and how they are found -- the tier the
/// person standing at the machine owns.
/// </summary>
public sealed record StationLinkAppConfig
{
    public string LocalFolderPath { get; init; } = "";
    public string? FilePattern { get; init; }
    public bool DirStructuredByDate { get; init; }
    public string? DateGranularity { get; init; }
    public string? MonthDirFormat { get; init; }
    public string ListingStrategy { get; init; } = ListingStrategies.Enumerate;
    public string? DirectFetchPrefix { get; init; }
    public int? DirectFetchIntervalMinutes { get; init; }
    public string? DirectFetchDatetimeFormat { get; init; }
    public string? DirectFetchDatetimeTimezone { get; init; }
    public string? DirectFetchFileExtension { get; init; }
    public int StabilityWindowSeconds { get; init; } = 60;

    /// <summary>The stability window as the readiness probe wants it.</summary>
    /// <remarks>
    /// Kept off the wire. This record is what the config endpoint will accept
    /// when the app starts writing its tier back, and ADL refuses any key
    /// outside the app-editable list -- a convenience property serialising
    /// itself into the body would turn every write into a 400.
    /// </remarks>
    [JsonIgnore]
    public TimeSpan StabilityWindow => TimeSpan.FromSeconds(StabilityWindowSeconds);
}

/// <summary>
/// How the agent finds a station's files. ADL's vocabulary, verbatim.
/// </summary>
/// <remarks>
/// These are the values ADL stores and sends, not a spelling chosen here:
/// the plugin's <c>AgentListingStrategy</c> is a Django <c>TextChoices</c>
/// whose stored form is lower case, and the label an administrator sees in
/// the admin ("Enumerate — scan the folder...") is a different string
/// entirely. Getting this wrong is not a cosmetic mistake: a link whose
/// strategy the agent does not recognise is one it will not scan, and a
/// fleet would go quiet everywhere at once.
/// </remarks>
public static class ListingStrategies
{
    public const string Enumerate = "enumerate";
    public const string DirectFetch = "direct_fetch";

    /// <summary>
    /// True when this station's files are found by walking its folder.
    /// </summary>
    /// <remarks>
    /// Case is ignored deliberately. The cost of being wrong is a station
    /// that silently collects nothing, and there is no reading of
    /// "ENUMERATE" from an ADL instance that could mean anything else.
    /// </remarks>
    public static bool IsEnumerate(string? strategy) =>
        string.IsNullOrWhiteSpace(strategy) ||
        string.Equals(strategy, Enumerate, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this station's filenames are built rather than found.</summary>
    public static bool IsDirectFetch(string? strategy) =>
        string.Equals(strategy, DirectFetch, StringComparison.OrdinalIgnoreCase);
}

public sealed record StationLinkAdminConfig
{
    public bool Enabled { get; init; } = true;
    public string Timezone { get; init; } = "UTC";
    public DateTimeOffset? StartDate { get; init; }
    public StationSummary Station { get; init; } = new();
}

public sealed record StationSummary
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string StationId { get; init; } = "";
    public string? WigosId { get; init; }
}
