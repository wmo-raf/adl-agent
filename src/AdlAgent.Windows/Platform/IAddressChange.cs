namespace AdlAgent.Windows.Platform;

/// <summary>
/// Asking the operating system to change where this machine reports.
/// </summary>
/// <remarks>
/// The tray's manifest is <c>asInvoker</c> and stays that way: a technician
/// without administrator rights is who that window is for (story 3), and a
/// program cannot elevate itself in any case. So this is not a method that
/// writes a setting -- it is a request to run <see cref="SetUrl"/> elevated,
/// which Windows answers with a consent prompt of its own.
/// <para>
/// That prompt is the design rather than a wart. Redirecting where a
/// country's observations are sent is an administrative act, and the
/// alternative -- a sixth control command letting the service write the
/// address on the tray's behalf -- would let anything running in an
/// interactive session point the agent at a host of its choosing, silently,
/// while the window went on looking healthy. The pipe can pair this device
/// and rebind a station's folder; it must not be able to move the machine.
/// </para>
/// <para>
/// An interface because the alternative in a test is a suite that raises a
/// consent prompt somebody has to click, on a machine that usually has no
/// installed agent to repoint.
/// </para>
/// </remarks>
public interface IAddressChange
{
    /// <summary>
    /// Ask for this machine to be pointed at <paramref name="adlBaseUrl"/>,
    /// and wait for the answer.
    /// </summary>
    /// <param name="keepPairing">
    /// True only when somebody has said this is the same ADL at a new
    /// address. The token is cleared otherwise, which is the safe default:
    /// a token issued by one instance means nothing to another.
    /// </param>
    Task<AddressChange> RequestAsync(
        string adlBaseUrl, bool keepPairing, CancellationToken cancellationToken = default);
}

/// <summary>What came of asking, and the sentence to show for it.</summary>
/// <remarks>
/// A result rather than an exception, because all three are things a window
/// draws. An administrator declining a prompt is not a fault; it is somebody
/// deciding not to, which is exactly what the prompt is there for.
/// </remarks>
/// <param name="Outcome">Which of the three happened.</param>
/// <param name="Detail">
/// What to show. Empty on a change that went through, where the window has
/// more to say about it than this seam does.
/// </param>
public sealed record AddressChange(AddressChangeOutcome Outcome, string Detail);

/// <summary>The three answers Windows can give.</summary>
public enum AddressChangeOutcome
{
    /// <summary>
    /// The verb ran elevated and reported success: the address is written and
    /// the service is running again.
    /// </summary>
    Changed,

    /// <summary>
    /// Nobody consented. Nothing on this machine was touched -- the verb
    /// never started -- and the window says so and stays open.
    /// </summary>
    Declined,

    /// <summary>
    /// It did not happen, and <see cref="AddressChange.Detail"/> says why.
    /// </summary>
    /// <remarks>
    /// Both halves of "no" that are not a decline: an address the window
    /// would not send in the first place, and a verb that started and could
    /// not finish. They are one case because the window does the same thing
    /// with them -- stay open, show the line -- and the line itself is what
    /// tells them apart.
    /// <para>
    /// It does not promise the machine is untouched. The verb writes the
    /// address before it restarts the service, so a failure after that point
    /// leaves an address written and a service stopped; its own output is the
    /// only thing that knows which, and that is why the sentence sends
    /// somebody to run it in a window where they can read it.
    /// </para>
    /// </remarks>
    Refused,
}
