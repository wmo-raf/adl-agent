namespace AdlAgent.Core.Update;

/// <summary>What the last update check came to.</summary>
/// <remarks>
/// Every path through <see cref="UpdateService"/> ends in one of these, and
/// they are deliberately not collapsed into "ok" and "failed". A machine
/// that is up to date, a machine an operator has pinned, a machine whose
/// instance holds no release yet and a machine that could not be reached all
/// look identical from a distance and want four different things done about
/// them -- nothing, nothing, an upload to the admin, and a look at the link.
/// </remarks>
public enum UpdateOutcome
{
    /// <summary>Not paired, or paired and since revoked. Nobody to ask.</summary>
    NotPaired,

    /// <summary>ADL was not reachable. The normal condition on these links.</summary>
    Unreachable,

    /// <summary>This instance serves no update feed -- an ADL older than the agent plugin's feed.</summary>
    NoFeed,

    /// <summary>ADL has nothing for this machine: no published release, or a pin it cannot serve.</summary>
    NothingOffered,

    /// <summary>What ADL offers is what this machine already runs.</summary>
    UpToDate,

    /// <summary>An operator has pinned this machine below the version it runs. It stays put.</summary>
    Held,

    /// <summary>Newer, and deliberately not applied: auto-update is off, or this install cannot replace itself.</summary>
    Available,

    /// <summary>The package was fetched, verified, and handed to the platform's installer.</summary>
    Applying,

    /// <summary>Something went wrong. <see cref="UpdateReport.Detail"/> says what.</summary>
    Failed,
}

/// <summary>One update check, as it turned out.</summary>
/// <param name="At">When the check ran.</param>
/// <param name="Outcome">What it came to.</param>
/// <param name="OfferedVersion">What ADL offered, when it offered anything.</param>
/// <param name="Pinned">Whether an operator has pinned this machine.</param>
/// <param name="Detail">
/// One sentence, for the log and the technician's window. ADL's own words
/// where ADL had any -- it knows why it is offering nothing and the agent
/// does not.
/// </param>
public sealed record UpdateReport(
    DateTimeOffset At,
    UpdateOutcome Outcome,
    string? OfferedVersion,
    bool Pinned,
    string Detail)
{
    /// <summary>Before any check has run.</summary>
    public static UpdateReport None { get; } =
        new(default, UpdateOutcome.NotPaired, null, false, "");
}
