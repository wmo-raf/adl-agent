namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// One pass in full, or the honest absence of it.
/// </summary>
/// <remarks>
/// A record wrapping a nullable rather than a bare answer, because "that
/// pass is no longer on this machine" has to be sayable. A window is opened,
/// a row is read, and the technician goes to make tea; by the time they open
/// the row the machine may have written its ceiling's worth of new passes
/// over it. That is an ordinary Tuesday on a machine working through a
/// backlog, and it is not a fault to report -- it is a sentence to show.
/// </remarks>
public sealed record CyclePass
{
    public CycleRecord? Record { get; init; }
}
