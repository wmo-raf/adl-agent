using AdlAgent.Core.Api;

namespace AdlAgent.Core.Cycle;

/// <summary>
/// How much of this machine's collection may happen at once.
/// </summary>
/// <remarks>
/// Two numbers, because there are two resources and they are not the same
/// one. Walking folders is disk, and the disk belongs to the machine: whether
/// this is one spinning drive or an array is something HQ cannot see from
/// another country, so <see cref="Units"/> is the agent's own. Uploading is
/// the link, and the link is shared by everything the country does, so its
/// bound is served by ADL and merely clamped here.
/// <para>
/// They must not multiply. Eight units each sending four files would be
/// thirty-two uploads in flight on a DSL line, which is slower than four and
/// harder on the ADL at the other end -- so the upload bound is one count
/// across every unit on the machine rather than one each.
/// </para>
/// <para>
/// Injected rather than compiled in, and set to one apiece by the tests. The
/// same calls go out in the same order at one as they did before any of this
/// was concurrent, so every sequence a test had already pinned still holds,
/// and what concurrency itself does is asserted where it is the subject.
/// </para>
/// </remarks>
public sealed class CycleConcurrency
{
    /// <summary>How many units may be collecting at once.</summary>
    /// <remarks>
    /// Two. Small on purpose: on the single disk these vendor servers
    /// usually have, more walks at once is not more throughput, and the point
    /// of running units in parallel is that one station's year of backlog
    /// stops holding up every other station's morning -- which two achieves
    /// and eight does not improve on.
    /// </remarks>
    public int Units { get; init; } = 2;

    /// <summary>
    /// The most uploads this machine will run at once, whatever ADL asks for.
    /// </summary>
    /// <remarks>
    /// A clamp rather than the number itself. ADL owns the figure because ADL
    /// is the party that knows the country's link and its own capacity; what
    /// this stops is a mistyped setting in an admin somewhere turning into a
    /// thousand sockets on a machine nobody can log into.
    /// </remarks>
    public int MostUploads { get; init; } = 32;

    /// <summary>
    /// How many uploads may be in flight across every unit, given what ADL
    /// last served.
    /// </summary>
    /// <remarks>
    /// Clamped, never rejected. A limit ADL did not send at all arrives here
    /// as the default on <see cref="AgentLimits"/>, which is the right
    /// reading of silence from an instance that predates the field: the same
    /// arrangement the reconciliation interval and the dated-folder window
    /// already have.
    /// </remarks>
    public int UploadsFor(AgentLimits limits) =>
        Math.Clamp(limits.ConcurrentUploads, 1, Math.Max(1, MostUploads));
}
