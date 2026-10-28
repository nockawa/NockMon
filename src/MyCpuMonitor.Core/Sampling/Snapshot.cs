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

    public readonly double GetValue(CoreMetric metric) => metric switch
    {
        CoreMetric.Usage => Usage,
        CoreMetric.User => User,
        CoreMetric.Kernel => Kernel,
        CoreMetric.Dpc => Dpc,
        CoreMetric.Interrupt => Interrupt,
        CoreMetric.InterruptRate => InterruptRate,
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
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
    };
}

/// <summary>One computed tick. Reused (double-buffered) by the engine: never keep a reference outside a read callback.</summary>
public sealed class Snapshot
{
    /// <summary>History index of this sample, -1 before the first one.</summary>
    public long SampleIndex = -1;
    public DateTime WallTimeUtc;
    public TimeSpan Interval;
    public int LogicalProcessors;
    public ProcessStats[] Processes = new ProcessStats[512];
    public int ProcessCount;
    public CoreStats[] Cores = [];
    public SystemStats System;

    public ReadOnlySpan<ProcessStats> ProcessSpan => Processes.AsSpan(0, ProcessCount);
}
