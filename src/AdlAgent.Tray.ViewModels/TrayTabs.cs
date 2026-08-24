namespace AdlAgent.Tray;

/// <summary>
/// The window's three tabs, and which of them a machine should open on.
/// </summary>
/// <remarks>
/// The tray used to open on Pairing whatever the machine was, so a technician
/// on a machine that paired months ago was shown the one screen with nothing
/// on it to do. The tab is chosen once, from the first answer the service
/// gives, and never moved again: a window that re-picked on every poll would
/// take somebody off the tab they had just opened, five seconds after they
/// opened it.
/// <para>
/// Indices rather than a type of their own, because that is what
/// <c>TabControl.SelectedIndex</c> binds to and a converter between the two
/// would be a second place for the order to be wrong.
/// </para>
/// </remarks>
public static class TrayTabs
{
    /// <summary>Where a pairing code is pasted. The window's first tab.</summary>
    public const int Pairing = 0;

    /// <summary>The station list and the folder each one is bound to.</summary>
    public const int Stations = 1;

    /// <summary>What this machine is, where it sends, and what went wrong last.</summary>
    public const int Status = 2;

    /// <summary>
    /// The tab that matches what this machine is, read off the line it is
    /// already showing.
    /// </summary>
    /// <remarks>
    /// From <see cref="NextStep.Kind"/> rather than from the status snapshot
    /// again. Both questions -- what to tell somebody to do, and which screen
    /// to do it on -- are answered by the same handful of facts in the same
    /// order, and two cascades over those facts are two places for them to
    /// come apart: a window opening on Pairing while its own line says the
    /// machine has no ADL address.
    /// </remarks>
    public static int For(NextStep step) => step.Kind switch
    {
        // A machine with no address has nothing to pair with and nothing to
        // bind. What it needs is the tab that says where it would send and
        // what to do about the fact that it cannot.
        NextStepKind.NotConfigured => Status,

        NextStepKind.NotPaired or NextStepKind.RePairNeeded => Pairing,

        // Including the two states nobody should reach here in -- nothing
        // heard from the service, and no service to hear from -- because the
        // caller does not choose a tab until it has an answer, and the first
        // tab is where the window already is.
        NextStepKind.Unknown or NextStepKind.ServiceNotRunning => Pairing,

        _ => Stations,
    };
}
