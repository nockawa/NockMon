namespace MyCpuMonitor.Core.Sampling;

/// <summary>Cumulative counters as read from the OS. Times are in 100 ns units.</summary>
public struct RawProcess
{
    public int Pid;
    public int ParentPid;
    public long CreateTime;
    public string Name;
    public long UserTime;
    public long KernelTime;
    public long WorkingSet;
    public long PrivateWorkingSet;
    public long PrivateBytes;
    public long VirtualSize;
    public long PagedPool;
    public long NonPagedPool;
    public long ReadOps;
    public long WriteOps;
    public long OtherOps;
    public long ReadBytes;
    public long WriteBytes;
    public long OtherBytes;
    public uint PageFaults;
    public uint HardFaults;
    public int Handles;
    public int Threads;
    public int SessionId;
    public int BasePriority;
}

public struct RawCore
{
    /// <summary>Includes IdleTime.</summary>
    public long KernelTime;
    public long UserTime;
    public long IdleTime;
    public long DpcTime;
    public long InterruptTime;
    public uint InterruptCount;
}

/// <summary>Sizes in bytes.</summary>
public struct RawMemory
{
    public long PhysicalTotal;
    public long PhysicalAvailable;
    public long CommitTotal;
    public long CommitLimit;
    public long SystemCache;
    public long KernelPaged;
    public long KernelNonPaged;
    public int Handles;
    public int Processes;
    public int Threads;
}

/// <summary>One probe read. Reused across ticks: arrays only grow.</summary>
public sealed class RawSample
{
    /// <summary>Monotonic time of the read.</summary>
    public TimeSpan Elapsed;
    public DateTime WallTimeUtc;
    public RawProcess[] Processes = new RawProcess[512];
    public int ProcessCount;
    public RawCore[] Cores = [];
    public RawMemory Memory;

    public void EnsureProcessCapacity(int count)
    {
        if (Processes.Length < count)
            Array.Resize(ref Processes, Math.Max(count, Processes.Length * 2));
    }

    public void EnsureCoreCount(int count)
    {
        if (Cores.Length != count)
            Cores = new RawCore[count];
    }
}
