namespace NockMon.Core.Etw;

/// <summary>System-wide event totals accumulated between two sample harvests.</summary>
public struct KernelEventTotals
{
    public long DiskReadBytes;
    public long DiskWriteBytes;
    public long DiskOps;
    public double DiskLatencySumMs;
    public long NetSendBytes;
    public long NetRecvBytes;
    public long DpcCount;
    public double DpcTimeMs;
    public double DpcMaxMs;
    public long IsrCount;
    public double IsrTimeMs;
    public double IsrMaxMs;
}

/// <summary>Per-process I/O totals accumulated between two scan harvests.</summary>
public struct ProcessIoTotals
{
    public long DiskReadBytes;
    public long DiskWriteBytes;
    public long DiskOps;
    public double DiskLatencySumMs;
    public long NetSendBytes;
    public long NetRecvBytes;
}

/// <summary>
/// Per-process .NET runtime activity accumulated between two scan harvests, and the managed heap size after the
/// process's last GC (held: a harvest does not reset it).
/// </summary>
public struct RuntimeTotals
{
    public long Gcs;
    public long Gen2Gcs;
    /// <summary>Time the managed threads were suspended for GCs, in ms (suspensions ended in the interval).</summary>
    public double PauseMs;
    public double PauseMaxMs;
    public long AllocatedBytes;
    /// <summary>
    /// Bytes released by the GCs ended in the interval. Derived, so it can come out slightly negative for an interval
    /// (allocation ticks lag by up to ~100 KB): consumers clamp; the sum over intervals stays exact.
    /// </summary>
    public long FreedBytes;
    /// <summary>Bytes that survived the GCs ended in the interval, all condemned generations.</summary>
    public long PromotedBytes;
    public long Exceptions;
    public long Contentions;
    /// <summary>Managed heap size after the last GC (all generations, large and pinned object heaps); 0 until one.</summary>
    public long HeapBytes;
}

/// <summary>Per-driver DPC/ISR totals accumulated between two scan harvests.</summary>
public struct DriverTotals
{
    public string Name;
    public long DpcCount;
    public double DpcTimeMs;
    public double DpcMaxMs;
    public long IsrCount;
    public double IsrTimeMs;
    public double IsrMaxMs;
}

public enum ProcessEventKind
{
    Start,
    Exit,
}

public sealed record ProcessEvent(DateTime TimeUtc, ProcessEventKind Kind, int Pid, int ParentPid, string Name, string? CommandLine, int? ExitCode);

/// <summary>Kernel event stream (ETW). Harvest methods reset what they return; all members are thread-safe.</summary>
public interface IKernelEventSource : IDisposable
{
    /// <summary>Null when running; otherwise why events are unavailable (not elevated, session failure).</summary>
    string? Status { get; }

    /// <summary>Events lost by the session since start (buffers overflowed).</summary>
    long EventsLost { get; }

    void HarvestSample(out KernelEventTotals totals);

    /// <summary>Moves per-process and per-driver totals into the given collections (cleared first).</summary>
    void HarvestScan(Dictionary<int, ProcessIoTotals> processes, List<DriverTotals> drivers);

    /// <summary>
    /// Copies the .NET runtime totals of each .NET process seen into <paramref name="processes"/> (cleared first), then
    /// resets the accumulated ones (the heap size holds) and forgets exited processes.
    /// </summary>
    void HarvestRuntime(Dictionary<int, RuntimeTotals> processes);

    /// <summary>Copies the most recent process events, newest last, into <paramref name="destination"/> (cleared first).</summary>
    void CopyRecentEvents(List<ProcessEvent> destination);
}
