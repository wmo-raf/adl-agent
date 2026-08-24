namespace AdlAgent.Tray;

/// <summary>
/// What the dot in the notification area means. Three states, because there
/// are three different things to do about them.
/// </summary>
/// <remarks>
/// Not decided by the icon. Every colour here is carried by the
/// <see cref="NextStep"/> the window is showing, so the dot and the sentence
/// cannot disagree: there is no path by which the corner of the screen goes
/// amber while the line at the top of the window says there is nothing to do.
/// </remarks>
public enum TrayState
{
    /// <summary>Nothing has been heard from the service yet.</summary>
    Unknown,

    /// <summary>Paired, synced, and ADL is answering. Nothing to do.</summary>
    Working,

    /// <summary>Running, but something wants a person: unpaired, revoked, or ADL unreachable.</summary>
    NeedsAttention,

    /// <summary>The service is not running. Nothing is being collected.</summary>
    Stopped,
}
