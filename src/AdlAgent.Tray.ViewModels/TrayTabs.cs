using AdlAgent.Core.Status;
using CorePairingState = AdlAgent.Core.Pairing.PairingState;

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
    public const int Pairing = 0;

    public const int Stations = 1;

    public const int Status = 2;

    /// <summary>The tab that matches what this machine is.</summary>
    public static int For(AgentStatusSnapshot status)
    {
        // A machine with no address has nothing to pair with and nothing to
        // bind. What it needs is the tab that says where it would send and
        // what to do about the fact that it cannot.
        if (!status.Configured)
        {
            return Status;
        }

        if (status.RePairNeeded || status.PairingState != nameof(CorePairingState.Paired))
        {
            return Pairing;
        }

        return Stations;
    }
}
