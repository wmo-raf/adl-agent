using AdlAgent.Core.Api;

namespace AdlAgent.Core.Heartbeat;

/// <summary>
/// The last completed cycle, held in memory for the heartbeat to read.
/// </summary>
/// <remarks>
/// In memory and not on disk, because the fact it holds is about this run:
/// after a restart the honest answer to "when did a cycle last finish" is
/// "not since I started", and ADL's own cycle-stuck check reads it that way.
/// <para>
/// Written by the scan cycle when it lands; until then a fresh install
/// reports no cycle, which is exactly what it has.
/// </para>
/// </remarks>
public sealed class CycleReportStore : ICycleReportSource
{
    private readonly Lock _gate = new();

    private CycleReport? _last;
    private int? _backlog;

    public CycleReport? LastCompletedCycle
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    public int? BacklogCount
    {
        get
        {
            lock (_gate)
            {
                return _backlog;
            }
        }
    }

    /// <summary>Record a cycle that ran to completion.</summary>
    public void Record(CycleReport cycle, int? backlogCount = null)
    {
        lock (_gate)
        {
            _last = cycle;
            _backlog = backlogCount;
        }
    }
}
