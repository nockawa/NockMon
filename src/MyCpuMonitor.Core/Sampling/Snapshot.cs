using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Model;

namespace MyCpuMonitor.Core.Sampling;

/// <summary>Per-process values for one tick. CPU in cores, rates per second, sizes in bytes.</summary>
public struct ProcessStats
{
    public int Pid;
    public int ParentPid;
    public long CreateTime;
    public string Name;
    public double Cpu;
    public double CpuUser;
    public double CpuKernel;
    public long WorkingSet;
    public long PrivateWorkingSet;
    public long PrivateBytes;
    public long VirtualSize;
    public long PagedPool;
    public long NonPagedPool;
    public double IoReadOps;
    public double IoWriteOps;
    public double IoOtherOps;
    public double IoReadBytes;
    public double IoWriteBytes;
    public double IoOtherBytes;
    public double PageFaults;
    public double HardFaults;
    public int Handles;
    public int Threads;
    public int SessionId;
    public int BasePriority;

    // Kernel events (ETW): physical disk and network, rates over the scan interval.
    public double DiskReadBytes;
    public double DiskWriteBytes;
    public double DiskOps;
    public double DiskLatencyMs;
    public double NetSendBytes;
    public double NetRecvBytes;

    /// <summary>GPU utilization (%) over the scan interval (NVML, NVIDIA GPUs).</summary>
    public double GpuPercent;

    public readonly ProcessKey Key => new(Pid, CreateTime);
    public readonly double IoOps => IoReadOps + IoWriteOps + IoOtherOps;
    public readonly double IoBytes => IoReadBytes + IoWriteBytes + IoOtherBytes;

    /// <summary>Numeric value of a field (NaN for <see cref="ProcessField.Name"/>). StartTime is in FILETIME ticks.</summary>
    public readonly double GetValue(ProcessField field) => field switch
    {
        ProcessField.Name => double.NaN,
        ProcessField.Pid => Pid,
        ProcessField.ParentPid => ParentPid,
        ProcessField.SessionId => SessionId,
        ProcessField.BasePriority => BasePriority,
        ProcessField.StartTime => CreateTime,
        ProcessField.Instances => 1,
        ProcessField.Cpu => Cpu,
        ProcessField.CpuUser => CpuUser,
        ProcessField.CpuKernel => CpuKernel,
        ProcessField.PrivateBytes => PrivateBytes,
        ProcessField.WorkingSet => WorkingSet,
        ProcessField.PrivateWorkingSet => PrivateWorkingSet,
        ProcessField.VirtualSize => VirtualSize,
        ProcessField.PagedPool => PagedPool,
        ProcessField.NonPagedPool => NonPagedPool,
        ProcessField.IoOps => IoOps,
        ProcessField.IoReadOps => IoReadOps,
        ProcessField.IoWriteOps => IoWriteOps,
        ProcessField.IoOtherOps => IoOtherOps,
        ProcessField.IoBytes => IoBytes,
        ProcessField.IoReadBytes => IoReadBytes,
        ProcessField.IoWriteBytes => IoWriteBytes,
        ProcessField.IoOtherBytes => IoOtherBytes,
        ProcessField.Handles => Handles,
        ProcessField.Threads => Threads,
        ProcessField.PageFaults => PageFaults,
        ProcessField.HardFaults => HardFaults,
        ProcessField.DiskRead => DiskReadBytes,
        ProcessField.DiskWrite => DiskWriteBytes,
        ProcessField.DiskBytes => DiskReadBytes + DiskWriteBytes,
        ProcessField.DiskOps => DiskOps,
        ProcessField.DiskLatency => DiskLatencyMs,
        ProcessField.NetSend => NetSendBytes,
        ProcessField.NetRecv => NetRecvBytes,
        ProcessField.NetBytes => NetSendBytes + NetRecvBytes,
        ProcessField.Gpu => GpuPercent,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };
}

public enum DriverField
{
    Name,
    DpcTime,
    DpcRate,
    DpcMax,
    IsrTime,
    IsrRate,
    IsrMax,
}

/// <summary>DPC/ISR activity of one driver over the scan interval. Times in ms per second (1000 = one CPU).</summary>
public struct DriverStats
{
    public string Name;
    public double DpcTime;
    public double DpcRate;
    public double DpcMaxMs;
    public double IsrTime;
    public double IsrRate;
    public double IsrMaxMs;

    public readonly double GetValue(DriverField field) => field switch
    {
        DriverField.Name => double.NaN,
        DriverField.DpcTime => DpcTime,
        DriverField.DpcRate => DpcRate,
        DriverField.DpcMax => DpcMaxMs,
        DriverField.IsrTime => IsrTime,
        DriverField.IsrRate => IsrRate,
        DriverField.IsrMax => IsrMaxMs,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };
}

/// <summary>Per logical processor, as fractions of that processor's time (0..1, i.e. cores).</summary>
public struct CoreStats
{
    public double Usage;
    public double User;
    public double Kernel;
    public double Dpc;
    public double Interrupt;
    public double InterruptRate;
    public double FrequencyMhz;

    public readonly double GetValue(CoreMetric metric) => metric switch
    {
        CoreMetric.Usage => Usage,
        CoreMetric.User => User,
        CoreMetric.Kernel => Kernel,
        CoreMetric.Dpc => Dpc,
        CoreMetric.Interrupt => Interrupt,
        CoreMetric.InterruptRate => InterruptRate,
        CoreMetric.Frequency => FrequencyMhz,
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
    };
}

public struct SystemStats
{
    public double Cpu;
    public double CpuUser;
    public double CpuKernel;
    public double CpuDpc;
    public double CpuInterrupt;
    public long PhysicalTotal;
    public long PhysicalAvailable;
    public long CommitTotal;
    public long CommitLimit;
    public long SystemCache;
    public long KernelPaged;
    public long KernelNonPaged;
    public int Processes;
    public int Threads;
    public int Handles;
    public double IoOps;
    public double IoBytes;
    public double ContextSwitches;
    public double SystemCalls;
    public double Interrupts;
    public double PageFaults;
    public double PageReads;
    public long MemoryZeroed;
    public long MemoryFree;
    public long MemoryStandby;
    public long MemoryModified;
    public double TimerResolutionMs;
    public double FrequencyMhz;
    public double ProcessorQueueLength;
    public double DiskRead;
    public double DiskWrite;
    public double DiskOps;
    public double DiskLatencyMs;
    public double NetSend;
    public double NetRecv;
    public double DpcRate;
    public double IsrRate;
    public double DpcMaxMs;
    public double IsrMaxMs;

    public readonly long PhysicalUsed => PhysicalTotal - PhysicalAvailable;

    public readonly double GetValue(SystemMetric metric) => metric switch
    {
        SystemMetric.Cpu => Cpu,
        SystemMetric.CpuUser => CpuUser,
        SystemMetric.CpuKernel => CpuKernel,
        SystemMetric.CpuDpc => CpuDpc,
        SystemMetric.CpuInterrupt => CpuInterrupt,
        SystemMetric.PhysicalUsed => PhysicalUsed,
        SystemMetric.PhysicalAvailable => PhysicalAvailable,
        SystemMetric.CommitTotal => CommitTotal,
        SystemMetric.CommitLimit => CommitLimit,
        SystemMetric.SystemCache => SystemCache,
        SystemMetric.KernelPaged => KernelPaged,
        SystemMetric.KernelNonPaged => KernelNonPaged,
        SystemMetric.Processes => Processes,
        SystemMetric.Threads => Threads,
        SystemMetric.Handles => Handles,
        SystemMetric.IoOps => IoOps,
        SystemMetric.IoBytes => IoBytes,
        SystemMetric.ContextSwitches => ContextSwitches,
        SystemMetric.SystemCalls => SystemCalls,
        SystemMetric.Interrupts => Interrupts,
        SystemMetric.PageFaults => PageFaults,
        SystemMetric.PageReads => PageReads,
        SystemMetric.MemoryZeroed => MemoryZeroed,
        SystemMetric.MemoryFree => MemoryFree,
        SystemMetric.MemoryStandby => MemoryStandby,
        SystemMetric.MemoryModified => MemoryModified,
        SystemMetric.TimerResolution => TimerResolutionMs,
        SystemMetric.CpuFrequency => FrequencyMhz,
        SystemMetric.ProcessorQueueLength => ProcessorQueueLength,
        SystemMetric.DiskRead => DiskRead,
        SystemMetric.DiskWrite => DiskWrite,
        SystemMetric.DiskOps => DiskOps,
        SystemMetric.DiskLatency => DiskLatencyMs,
        SystemMetric.NetSend => NetSend,
        SystemMetric.NetRecv => NetRecv,
        SystemMetric.DpcRate => DpcRate,
        SystemMetric.IsrRate => IsrRate,
        SystemMetric.DpcMax => DpcMaxMs,
        SystemMetric.IsrMax => IsrMaxMs,
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
    };
}

public enum ObjectTypeField
{
    Name,
    Objects,
    Handles,
    /// <summary>Objects per minute since the previous scan (sustained growth: a leak).</summary>
    ObjectGrowth,
    HandleGrowth,
    PeakObjects,
    PeakHandles,
}

/// <summary>Live objects of one kernel object type. Objects far above handles are referenced by the kernel itself.</summary>
public struct ObjectTypeStats
{
    public string Name;
    public long Objects;
    public long Handles;
    public long PeakObjects;
    public long PeakHandles;
    public double ObjectGrowth;
    public double HandleGrowth;

    public readonly double GetValue(ObjectTypeField field) => field switch
    {
        ObjectTypeField.Name => double.NaN,
        ObjectTypeField.Objects => Objects,
        ObjectTypeField.Handles => Handles,
        ObjectTypeField.ObjectGrowth => ObjectGrowth,
        ObjectTypeField.HandleGrowth => HandleGrowth,
        ObjectTypeField.PeakObjects => PeakObjects,
        ObjectTypeField.PeakHandles => PeakHandles,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };
}

public enum PoolTagField
{
    Tag,
    NonPagedBytes,
    PagedBytes,
    TotalBytes,
    /// <summary>Bytes per second (positive: growing).</summary>
    NonPagedGrowth,
    PagedGrowth,
    /// <summary>Allocations per second (paged + non-paged).</summary>
    AllocationRate,
    /// <summary>Allocations not freed (paged + non-paged).</summary>
    Outstanding,
}

/// <summary>Kernel pool usage by tag (scan rate). Growth that never comes back down is the signature of a driver leak.</summary>
public struct PoolTagStats
{
    public string Tag;
    public long NonPagedBytes;
    public long PagedBytes;
    public double NonPagedGrowth;
    public double PagedGrowth;
    public double AllocationRate;
    public long Outstanding;

    public readonly double GetValue(PoolTagField field) => field switch
    {
        PoolTagField.Tag => double.NaN,
        PoolTagField.NonPagedBytes => NonPagedBytes,
        PoolTagField.PagedBytes => PagedBytes,
        PoolTagField.TotalBytes => NonPagedBytes + PagedBytes,
        PoolTagField.NonPagedGrowth => NonPagedGrowth,
        PoolTagField.PagedGrowth => PagedGrowth,
        PoolTagField.AllocationRate => AllocationRate,
        PoolTagField.Outstanding => Outstanding,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };
}

/// <summary>
/// Result of one full scan (processes and pool tags). Produced at the scan rate, double-buffered independently of
/// <see cref="Snapshot"/>.
/// </summary>
public sealed class ProcessTable
{
    public PoolTagStats[] PoolTags = new PoolTagStats[4096];
    public int PoolTagCount;

    /// <summary>Drivers with DPC/ISR activity during the interval (ETW).</summary>
    public List<DriverStats> Drivers = [];

    public ObjectTypeStats[] ObjectTypes = new ObjectTypeStats[128];
    public int ObjectTypeCount;

    public ReadOnlySpan<ObjectTypeStats> ObjectTypeSpan => ObjectTypes.AsSpan(0, ObjectTypeCount);

    public ReadOnlySpan<PoolTagStats> PoolTagSpan => PoolTags.AsSpan(0, PoolTagCount);

    /// <summary>History scan index of this table, -1 before the first one.</summary>
    public long ScanIndex = -1;
    public DateTime WallTimeUtc;

    /// <summary>Time covered by the rates (previous scan to this one).</summary>
    public TimeSpan Interval;

    public ProcessStats[] Items = new ProcessStats[512];
    public int Count;

    /// <summary>Sum of all processes' I/O rates.</summary>
    public double IoOps;
    public double IoBytes;

    /// <summary>Threads ready to run, waiting for a CPU, at scan time.</summary>
    public int ReadyThreads;

    public ReadOnlySpan<ProcessStats> Span => Items.AsSpan(0, Count);
}

/// <summary>
/// One fast tick: system and per-CPU values at the sample interval, plus the latest process table (scan rate).
/// Reused (double-buffered) by the engine: never keep a reference outside a read callback.
/// </summary>
public sealed class Snapshot
{
    /// <summary>History index of this sample, -1 before the first one.</summary>
    public long SampleIndex = -1;
    public DateTime WallTimeUtc;
    public TimeSpan Interval;
    public int LogicalProcessors;
    public CoreStats[] Cores = [];
    public SystemStats System;

    /// <summary>Latest process scan. Shared between consecutive snapshots until the next scan.</summary>
    public ProcessTable Table = new();

    /// <summary>One entry per GPU adapter (sample rate).</summary>
    public List<Gpu.GpuStats> Gpus = [];

    public ProcessStats[] Processes => Table.Items;
    public int ProcessCount => Table.Count;
    public ReadOnlySpan<ProcessStats> ProcessSpan => Table.Span;
}
