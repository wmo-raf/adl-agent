namespace AdlAgent.Core.Cycle;

/// <summary>
/// What a collect asked for at the machine is doing, as the window watching
/// it draws.
/// </summary>
/// <remarks>
/// Polled rather than streamed, and that is a constraint rather than a taste:
/// the control surface serves one client at a time, so a command that held
/// its connection open for the length of an upload would freeze the tray's
/// own status poll -- and with it the header, the next-step line and the
/// colour of the icon in the corner -- for as long as the run took. A run
/// that reports itself in short answers to short questions leaves all of that
/// moving.
/// <para>
/// The counts are the run's own <see cref="LinkTally"/>, read at the moment
/// the question arrives. Nothing is copied forward as the run proceeds and
/// nothing accumulates here; a poll that lands between two files sees the
/// state between two files, which is exactly what it is asking about.
/// </para>
/// </remarks>
public sealed record CollectProgress
{
    /// <summary>The station this run is for.</summary>
    public required long StationLinkId { get; init; }

    public string StationName { get; init; } = "";

    public string ConnectionName { get; init; } = "";

    /// <summary>The folder and pattern being used, so the window can show what it is working on.</summary>
    public string LocalFolderPath { get; init; } = "";

    public string FilePattern { get; init; } = "";

    /// <summary>
    /// Which part of the cycle this is, in the words somebody watching it
    /// reads.
    /// </summary>
    /// <remarks>
    /// A sentence and not a percentage. The agent cannot know how many files
    /// a folder holds until it has walked it, and cannot know how many ADL
    /// will ask for until it has offered them, so a bar would be a number
    /// invented to fill a bar.
    /// </remarks>
    public string Step { get; init; } = "";

    /// <summary>True while the run is still going.</summary>
    public required bool Running { get; init; }

    /// <summary>True when somebody stopped it from the window.</summary>
    public bool Cancelled { get; init; }

    public int Scanned { get; init; }

    public int Offered { get; init; }

    public int Requested { get; init; }

    public int Uploaded { get; init; }

    public int Failed { get; init; }

    /// <summary>What went wrong, if anything did. Null while all is well.</summary>
    public string? Error { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When it stopped, or null while it has not.</summary>
    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>
/// What one collect asked for at the machine came to, kept for the row that
/// asked for it.
/// </summary>
/// <remarks>
/// Deliberately not written into <see cref="Heartbeat.CycleReportStore"/>,
/// which is the one machine-wide cycle the heartbeat tells ADL about. A run
/// covering a single station recorded there would reach HQ as a cycle that
/// had just finished having scanned one station of forty, and ADL's own
/// cycle-stuck and coverage checks would read that as the machine having
/// stopped collecting the rest.
/// <para>
/// So it lives beside the cycle rather than in it, and the row says which of
/// the two it is showing.
/// </para>
/// </remarks>
public sealed record RequestedCollect
{
    public required DateTimeOffset At { get; init; }

    public int Scanned { get; init; }

    public int Offered { get; init; }

    public int Uploaded { get; init; }

    public int Failed { get; init; }

    /// <summary>True when somebody stopped it before it finished.</summary>
    public bool Cancelled { get; init; }

    public string? Error { get; init; }
}
