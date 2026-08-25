namespace AdlAgent.Core.Status;

/// <summary>
/// Whether data is actually reaching ADL for one station.
/// </summary>
/// <remarks>
/// The question the station list could not answer until ADL began sending
/// <see cref="AgentStationSnapshot.LastReceivedAt"/>. Everything else a row
/// carries is about what this machine <em>did</em> -- files seen, files sent,
/// what went wrong -- and all of it reads healthy for a station that is
/// configured perfectly and sending nothing because the logger died, the
/// share unmounted, or the vendor changed what it writes and the pattern
/// stopped matching.
/// <para>
/// Decided in <see cref="AgentStationsReader"/> rather than in the window, so
/// that the verdict travels on the snapshot. Two things follow from that and
/// both are the point. The tray rebuilds its rows when the snapshot changes,
/// so a station crossing its window turns amber on the next poll rather than
/// waiting for some unrelated fact to move; and <c>StationStanding</c> stays
/// a pure function of a list, with no clock of its own to disagree with this
/// one.
/// </para>
/// </remarks>
public enum StationFlow
{
    /// <summary>
    /// Switched off in ADL, so there is nothing to judge.
    /// </summary>
    /// <remarks>
    /// Not a verdict but the absence of one, and drawn as such. Calling a
    /// switched-off station <see cref="Collecting"/> would be the list
    /// asserting that data is flowing for a station nothing is scanned or
    /// sent for; calling it <see cref="Quiet"/> would send a technician
    /// looking for a fault that is an administrator's deliberate choice.
    /// </remarks>
    NotJudged,

    /// <summary>ADL received something inside this station's window.</summary>
    Collecting,

    /// <summary>
    /// Nothing has reached ADL inside the window -- including nothing ever.
    /// </summary>
    /// <remarks>
    /// No error, no missing folder: the station is configured and silent,
    /// which is the state this whole verdict exists to name. A station that
    /// has never sent anything is quiet rather than a state of its own, so a
    /// technician who has just bound a folder watches the row turn green on
    /// the next cycle -- which is the commonest reason the window is open.
    /// </remarks>
    Quiet,

    /// <summary>
    /// Nothing can arrive, and it is visible from this machine.
    /// </summary>
    /// <remarks>
    /// No folder bound, or the last cycle reported an error for this station.
    /// One verdict for both because both are the technician's to act on and
    /// the row says which in its own words -- the folder column is empty, or
    /// the problem column is not.
    /// </remarks>
    Blocked,
}

/// <summary>How a station's flow is judged, in one place.</summary>
public static class StationFlows
{
    /// <summary>
    /// The window to use when ADL sent none.
    /// </summary>
    /// <remarks>
    /// Only reachable against an ADL that predates the field, since a current
    /// one folds its own default in before sending. Six hours because that is
    /// what ADL's default is; a machine that quietly used some other number
    /// would make two halves of one system disagree about the same station.
    /// </remarks>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(6);

    /// <summary>
    /// Judge one station, in the order the answers rank.
    /// </summary>
    /// <remarks>
    /// Switched off is asked first and answers <see cref="StationFlow.NotJudged"/>
    /// whatever else is true, because a station nothing is scanned for cannot
    /// be blocked or quiet in any sense a person could act on. Blocked
    /// outranks quiet because a station with no folder is necessarily also
    /// silent, and "bind a folder" is the more useful of the two things to be
    /// told.
    /// </remarks>
    public static StationFlow Of(
        bool enabled,
        string? localFolderPath,
        string? error,
        DateTimeOffset? lastReceivedAt,
        int? staleAfterMinutes,
        DateTimeOffset now)
    {
        if (!enabled)
        {
            return StationFlow.NotJudged;
        }

        if (string.IsNullOrWhiteSpace(localFolderPath) || !string.IsNullOrEmpty(error))
        {
            return StationFlow.Blocked;
        }

        if (lastReceivedAt is not { } received)
        {
            return StationFlow.Quiet;
        }

        return now - received <= Window(staleAfterMinutes)
            ? StationFlow.Collecting
            : StationFlow.Quiet;
    }

    /// <summary>
    /// This connection's window, or ADL's default when it stated none.
    /// </summary>
    /// <remarks>
    /// A nonsense number is treated as no number rather than honoured. Zero
    /// or below would make every station on the connection permanently quiet,
    /// which is a whole vendor's worth of rows saying "look here" about
    /// nothing -- and the fastest way to teach somebody to stop reading the
    /// column.
    /// </remarks>
    private static TimeSpan Window(int? staleAfterMinutes) =>
        staleAfterMinutes is > 0
            ? TimeSpan.FromMinutes(staleAfterMinutes.Value)
            : DefaultWindow;
}
