namespace NockMon.Core.Sampling;

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

    /// <summary>Effective/nominal frequency in % (PDH "% Processor Performance"); NaN when unavailable.</summary>
    public double PerformancePercent;
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

    /// <summary>Memory lists (bytes); -1 when unavailable (missing privilege).</summary>
    public long Zeroed;
    public long Free;
    public long Standby;
    public long Modified;
}

/// <summary>Kernel-wide counters. Cumulative counts are 32-bit and wrap.</summary>
public struct RawKernel
{
    public uint ContextSwitches;
    public uint SystemCalls;
    public uint PageFaults;
    public uint PageReads;

    /// <summary>Current global timer resolution, 100 ns units.</summary>
    public uint TimerResolution;

    /// <summary>Average "% processor performance" and nominal MHz; NaN when unavailable.</summary>
    public double PerformancePercent;
    public double NominalMhz;
}

/// <summary>Live objects and handles of one kernel object type (scan rate).</summary>
public struct RawObjectType
{
    public string Name;
    public uint Objects;
    public uint Handles;
    public uint PeakObjects;
    public uint PeakHandles;
}

/// <summary>One pool tag's cumulative counters (scan rate).</summary>
public struct RawPoolTag
{
    public uint Tag;
    public uint PagedAllocs;
    public uint PagedFrees;
    public long PagedUsed;
    public uint NonPagedAllocs;
    public uint NonPagedFrees;
    public long NonPagedUsed;
}

/// <summary>One probe read. Reused across ticks: arrays only grow.</summary>
public sealed class RawSample
{
    /// <summary>Monotonic time of the system read (<see cref="ISystemProbe.CollectSystem"/>).</summary>
    public TimeSpan Elapsed;
    public DateTime WallTimeUtc;

    /// <summary>Monotonic time of the last process scan (<see cref="ISystemProbe.CollectProcesses"/>).</summary>
    public TimeSpan ProcessesElapsed;
    public RawProcess[] Processes = new RawProcess[512];
    public int ProcessCount;

    /// <summary>Threads in the Ready state at the scan (waiting for a CPU: the processor queue).</summary>
    public int ReadyThreads;
    public RawCore[] Cores = [];
    public RawMemory Memory;
    public RawKernel Kernel;

    /// <summary>Kernel object types (scan rate).</summary>
    public RawObjectType[] ObjectTypes = new RawObjectType[128];
    public int ObjectTypeCount;

    /// <summary>Pool tags (scan rate, with the processes).</summary>
    public RawPoolTag[] PoolTags = new RawPoolTag[4096];
    public int PoolTagCount;

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
