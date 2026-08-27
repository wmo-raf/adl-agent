using AdlAgent.Core.Api;

namespace AdlAgent.Core.Heartbeat;

/// <summary>
/// What the scan cycle last did, as the heartbeat needs to report it.
/// </summary>
/// <remarks>
/// The seam between the two loops, and the reason they can stay apart. The
/// heartbeat never waits on the cycle, calls into it, or knows whether it is
/// running; it reads the last thing the cycle finished and says so. That is
/// what makes "the machine is up and its work has stopped" an observation ADL
/// can make at all -- a heartbeat that went through the cycle would go quiet
/// with it, and HQ would be back to guessing.
/// </remarks>
public interface ICycleReportSource
{
    /// <summary>
    /// Every station's latest word, stamped with the last pass that ran to
    /// completion -- or <c>null</c> if none has since this service started.
    /// </summary>
    /// <remarks>
    /// Not one pass's snapshot. Collection runs a unit at a time and each
    /// unit finishes on its own, so what is reported is each station's own
    /// most recent counts (wmo-raf/adl#304).
    /// </remarks>
    CycleReport? LastCompletedCycle { get; }

    /// <summary>When this station's own pass last finished, or null.</summary>
    DateTimeOffset? LastPassAt(long stationLinkId);

    /// <summary>
    /// Files this machine has seen and ADL has not yet accepted. <c>null</c>
    /// when the agent does not know -- which is not the same as zero, and is
    /// what a machine that has not managed a cycle yet should say.
    /// </summary>
    int? BacklogCount { get; }
}
