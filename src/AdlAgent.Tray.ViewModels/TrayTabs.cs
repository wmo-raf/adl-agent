namespace AdlAgent.Tray;

/// <summary>
/// The window's two tabs, and which of them a machine should open on.
/// </summary>
/// <remarks>
/// There were three. The first was Pairing, which held a code box a machine
/// uses once and a copy of four facts the Status tab already carried -- so a
/// technician on a server that paired months ago was given a whole screen
/// with nothing on it to do, in the leftmost and most prominent position.
/// Choosing the opening tab, which is what this class first existed for,
/// treated the symptom; the tab is now folded into the Status row that says
/// what this machine's pairing is, beneath the state it is the remedy for.
/// <para>
/// The tab is chosen once, from the first answer the service gives, and never
/// moved again: a window that re-picked on every poll would take somebody off
/// the tab they had just opened, five seconds after they opened it.
/// </para>
/// <para>
/// Indices rather than a type of their own, because that is what
/// <c>TabControl.SelectedIndex</c> binds to and a converter between the two
/// would be a second place for the order to be wrong.
/// </para>
/// </remarks>
public static class TrayTabs
{
    /// <summary>
    /// The station list and the folder each one is bound to. The window's
    /// first tab, because it is the one a working machine lives on.
    /// </summary>
    public const int Stations = 0;

    /// <summary>
    /// What this machine is, where it sends, what went wrong last -- and,
    /// when there is a pairing code to type, where it goes.
    /// </summary>
    public const int Status = 1;

    /// <summary>
    /// The tab that matches what this machine is, read off the line it is
    /// already showing.
    /// </summary>
    /// <remarks>
    /// One rule: a question about the machine opens on Status, a question
    /// about the work opens on Stations. Every state that wants somebody to
    /// do something to this server -- give it an address, pair it, pair it
    /// again -- is answered in one place now, so the cascade that used to
    /// sort them is a list.
    /// <para>
    /// From <see cref="NextStep.Kind"/> rather than from the status snapshot
    /// again. Both questions -- what to tell somebody to do, and which screen
    /// to do it on -- are answered by the same handful of facts in the same
    /// order, and two cascades over those facts are two places for them to
    /// come apart.
    /// </para>
    /// <para>
    /// <see cref="NextStepKind.Unknown"/> and
    /// <see cref="NextStepKind.ServiceNotRunning"/> are here for completeness
    /// and are not reached: the caller does not choose a tab until the
    /// service has answered. They are grouped with the rest on the honest
    /// grounds that a machine nobody can hear from is one you read about
    /// rather than one you work on.
    /// </para>
    /// </remarks>
    public static int For(NextStep step) => step.Kind switch
    {
        NextStepKind.NotConfigured
            or NextStepKind.NotPaired
            or NextStepKind.RePairNeeded
            or NextStepKind.Unknown
            or NextStepKind.ServiceNotRunning => Status,

        _ => Stations,
    };
}
