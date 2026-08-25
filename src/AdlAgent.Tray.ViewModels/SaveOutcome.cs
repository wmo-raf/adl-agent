namespace AdlAgent.Tray;

/// <summary>
/// What became of a save, in the three shapes a window has to behave
/// differently for.
/// </summary>
/// <remarks>
/// Returned rather than left for the window to work out from the control
/// link's answer. The window is <c>net10.0-windows</c>, which the test
/// project cannot reference, and "close on this one, stay open on that one"
/// is a decision -- the kind this repository keeps next door precisely so
/// something can drive it.
/// </remarks>
public enum SaveOutcome
{
    /// <summary>ADL took the settings. The window is done.</summary>
    Saved,

    /// <summary>
    /// ADL would not take them, and said why. The window stays open, because
    /// what it is showing is the thing that has to change.
    /// </summary>
    Refused,

    /// <summary>
    /// ADL has revoked this machine's token. Nothing typed in the window can
    /// be saved by anybody until it is paired again, so the window closes and
    /// the next-step line behind it takes over.
    /// </summary>
    MustRePair,
}
