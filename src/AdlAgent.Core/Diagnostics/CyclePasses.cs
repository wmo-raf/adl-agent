namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// The recent unit passes, as one answer to a local UI.
/// </summary>
/// <remarks>
/// <see cref="More"/> is here because the control surface reads one line at a
/// time with a cap on it, and a station on a busy machine can easily have
/// more recent detail than fits. A window that silently showed six passes
/// when ten were asked for would be a window that reads as "this machine has
/// only run six times", which is the exact misreading this whole feature
/// exists to stop.
/// </remarks>
public sealed record CyclePasses
{
    public required IReadOnlyList<CycleRecord> Passes { get; init; }

    /// <summary>True when older passes exist and did not fit in one answer.</summary>
    public bool More { get; init; }
}
