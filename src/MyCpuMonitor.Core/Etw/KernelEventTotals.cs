namespace MyCpuMonitor.Core.Etw;

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

    /// <summary>Copies the most recent process events, newest last, into <paramref name="destination"/> (cleared first).</summary>
    void CopyRecentEvents(List<ProcessEvent> destination);
}
