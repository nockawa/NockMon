namespace MyCpuMonitor.Core.Metrics;

public enum MetricUnit
{
    Text,
    /// <summary>CPU expressed in logical CPUs used (1.0 = one logical processor fully busy). Displayed according to <see cref="CpuScale"/>.</summary>
    Cpu,
    Bytes,
    BytesPerSecond,
    PerSecond,
    Count,
    Timestamp,
    Milliseconds,
    Megahertz,
}

/// <summary>How CPU values are displayed. Stored values are always in logical CPUs used.</summary>
public enum CpuScale
{
    /// <summary>Percentage of the machine: 100% = all logical processors busy (Task Manager).</summary>
    Machine,
    /// <summary>Logical CPUs used: 1.00 = one logical processor busy, max = logical processor count.</summary>
    Cpus,
}

/// <summary>Per-process values: table columns, sort keys and (for <see cref="MetricInfo.HasHistory"/>) chart series.</summary>
public enum ProcessField
{
    Name,
    Pid,
    ParentPid,
    SessionId,
    BasePriority,
    StartTime,
    /// <summary>Number of processes aggregated (name groups).</summary>
    Instances,
    Cpu,
    CpuUser,
    CpuKernel,
    PrivateBytes,
    WorkingSet,
    PrivateWorkingSet,
    VirtualSize,
    PagedPool,
    NonPagedPool,
    /// <summary>All I/O operations (file, network, device). Not disk-only.</summary>
    IoOps,
    IoReadOps,
    IoWriteOps,
    IoOtherOps,
    IoBytes,
    IoReadBytes,
    IoWriteBytes,
    IoOtherBytes,
    Handles,
    Threads,
    PageFaults,
    HardFaults,
}

public enum SystemMetric
{
    Cpu,
    CpuUser,
    CpuKernel,
    CpuDpc,
    CpuInterrupt,
    PhysicalUsed,
    PhysicalAvailable,
    CommitTotal,
    CommitLimit,
    SystemCache,
    KernelPaged,
    KernelNonPaged,
    Processes,
    Threads,
    Handles,
    IoOps,
    IoBytes,

    // Kernel (appended: history slots are indexed by value).
    ContextSwitches,
    SystemCalls,
    Interrupts,
    PageFaults,
    /// <summary>Pages read from disk to resolve hard faults.</summary>
    PageReads,
    MemoryZeroed,
    MemoryFree,
    MemoryStandby,
    MemoryModified,
    /// <summary>Global timer resolution (raised by apps calling timeBeginPeriod: costs power).</summary>
    TimerResolution,
    /// <summary>Effective frequency averaged over all CPUs.</summary>
    CpuFrequency,
    /// <summary>Threads ready to run but waiting for a CPU.</summary>
    ProcessorQueueLength,
}

public enum CoreMetric
{
    /// <summary>Fraction of the core busy, in cores (0..1).</summary>
    Usage,
    User,
    Kernel,
    Dpc,
    Interrupt,
    InterruptRate,
    /// <summary>Effective frequency (nominal × % processor performance).</summary>
    Frequency,
}

public readonly record struct MetricInfo(string DisplayName, string ShortName, MetricUnit Unit, bool HasHistory = false);

public static class MetricCatalog
{
    /// <summary>Process fields kept in history (aggregated per image name).</summary>
    public static readonly ProcessField[] ProcessHistoryFields =
    [
        ProcessField.Cpu, ProcessField.PrivateBytes, ProcessField.WorkingSet, ProcessField.IoOps, ProcessField.IoBytes,
        ProcessField.Handles, ProcessField.Threads, ProcessField.HardFaults, ProcessField.Instances,
    ];

    public static int HistoryIndexOf(ProcessField field) => Array.IndexOf(ProcessHistoryFields, field);

    /// <summary>Default table column width in effective pixels.</summary>
    public static double DefaultColumnWidth(ProcessField field) => field switch
    {
        ProcessField.Name => 180,
        ProcessField.Pid or ProcessField.ParentPid or ProcessField.SessionId or ProcessField.BasePriority or ProcessField.Instances => 64,
        ProcessField.StartTime => 120,
        _ => 84,
    };

    public static MetricInfo Get(ProcessField field) => field switch
    {
        ProcessField.Name => new("Name", "Name", MetricUnit.Text),
        ProcessField.Pid => new("Process ID", "PID", MetricUnit.Count),
        ProcessField.ParentPid => new("Parent process ID", "PPID", MetricUnit.Count),
        ProcessField.SessionId => new("Session", "Session", MetricUnit.Count),
        ProcessField.BasePriority => new("Base priority", "Prio", MetricUnit.Count),
        ProcessField.StartTime => new("Start time", "Started", MetricUnit.Timestamp),
        ProcessField.Instances => new("Instances", "Inst", MetricUnit.Count, true),
        ProcessField.Cpu => new("CPU", "CPU", MetricUnit.Cpu, true),
        ProcessField.CpuUser => new("CPU (user)", "User", MetricUnit.Cpu),
        ProcessField.CpuKernel => new("CPU (kernel)", "Kernel", MetricUnit.Cpu),
        ProcessField.PrivateBytes => new("Private bytes", "Private", MetricUnit.Bytes, true),
        ProcessField.WorkingSet => new("Working set", "WS", MetricUnit.Bytes, true),
        ProcessField.PrivateWorkingSet => new("Private working set", "Private WS", MetricUnit.Bytes),
        ProcessField.VirtualSize => new("Virtual size", "Virtual", MetricUnit.Bytes),
        ProcessField.PagedPool => new("Paged pool", "Paged", MetricUnit.Bytes),
        ProcessField.NonPagedPool => new("Non-paged pool", "NP pool", MetricUnit.Bytes),
        ProcessField.IoOps => new("I/O operations", "I/O ops", MetricUnit.PerSecond, true),
        ProcessField.IoReadOps => new("I/O reads", "Reads", MetricUnit.PerSecond),
        ProcessField.IoWriteOps => new("I/O writes", "Writes", MetricUnit.PerSecond),
        ProcessField.IoOtherOps => new("I/O other", "Other ops", MetricUnit.PerSecond),
        ProcessField.IoBytes => new("I/O throughput", "I/O", MetricUnit.BytesPerSecond, true),
        ProcessField.IoReadBytes => new("I/O read throughput", "Read", MetricUnit.BytesPerSecond),
        ProcessField.IoWriteBytes => new("I/O write throughput", "Write", MetricUnit.BytesPerSecond),
        ProcessField.IoOtherBytes => new("I/O other throughput", "Other", MetricUnit.BytesPerSecond),
        ProcessField.Handles => new("Handles", "Handles", MetricUnit.Count, true),
        ProcessField.Threads => new("Threads", "Threads", MetricUnit.Count, true),
        ProcessField.PageFaults => new("Page faults", "PF", MetricUnit.PerSecond),
        ProcessField.HardFaults => new("Hard faults", "Hard PF", MetricUnit.PerSecond, true),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    public static MetricInfo Get(SystemMetric metric) => metric switch
    {
        SystemMetric.Cpu => new("CPU", "CPU", MetricUnit.Cpu, true),
        SystemMetric.CpuUser => new("CPU (user)", "User", MetricUnit.Cpu, true),
        SystemMetric.CpuKernel => new("CPU (kernel)", "Kernel", MetricUnit.Cpu, true),
        SystemMetric.CpuDpc => new("CPU (DPC)", "DPC", MetricUnit.Cpu, true),
        SystemMetric.CpuInterrupt => new("CPU (interrupt)", "ISR", MetricUnit.Cpu, true),
        SystemMetric.PhysicalUsed => new("Physical memory used", "RAM used", MetricUnit.Bytes, true),
        SystemMetric.PhysicalAvailable => new("Physical memory available", "RAM avail", MetricUnit.Bytes, true),
        SystemMetric.CommitTotal => new("Commit charge", "Commit", MetricUnit.Bytes, true),
        SystemMetric.CommitLimit => new("Commit limit", "Commit limit", MetricUnit.Bytes, true),
        SystemMetric.SystemCache => new("System cache", "Cache", MetricUnit.Bytes, true),
        SystemMetric.KernelPaged => new("Kernel paged pool", "Paged pool", MetricUnit.Bytes, true),
        SystemMetric.KernelNonPaged => new("Kernel non-paged pool", "NP pool", MetricUnit.Bytes, true),
        SystemMetric.Processes => new("Processes", "Procs", MetricUnit.Count, true),
        SystemMetric.Threads => new("Threads", "Threads", MetricUnit.Count, true),
        SystemMetric.Handles => new("Handles", "Handles", MetricUnit.Count, true),
        SystemMetric.IoOps => new("I/O operations", "I/O ops", MetricUnit.PerSecond, true),
        SystemMetric.IoBytes => new("I/O throughput", "I/O", MetricUnit.BytesPerSecond, true),
        SystemMetric.ContextSwitches => new("Context switches", "Ctx sw", MetricUnit.PerSecond, true),
        SystemMetric.SystemCalls => new("System calls", "Syscalls", MetricUnit.PerSecond, true),
        SystemMetric.Interrupts => new("Interrupts", "Int", MetricUnit.PerSecond, true),
        SystemMetric.PageFaults => new("Page faults", "PF", MetricUnit.PerSecond, true),
        SystemMetric.PageReads => new("Hard fault page reads", "Page reads", MetricUnit.PerSecond, true),
        SystemMetric.MemoryZeroed => new("Zeroed memory", "Zeroed", MetricUnit.Bytes, true),
        SystemMetric.MemoryFree => new("Free memory", "Free", MetricUnit.Bytes, true),
        SystemMetric.MemoryStandby => new("Standby memory (cache)", "Standby", MetricUnit.Bytes, true),
        SystemMetric.MemoryModified => new("Modified memory", "Modified", MetricUnit.Bytes, true),
        SystemMetric.TimerResolution => new("Timer resolution", "Timer", MetricUnit.Milliseconds, true),
        SystemMetric.CpuFrequency => new("CPU frequency", "Freq", MetricUnit.Megahertz, true),
        SystemMetric.ProcessorQueueLength => new("Processor queue length", "Queue", MetricUnit.Count, true),
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
    };

    public static MetricInfo Get(CoreMetric metric) => metric switch
    {
        CoreMetric.Usage => new("Core usage", "Usage", MetricUnit.Cpu, true),
        CoreMetric.User => new("Core user time", "User", MetricUnit.Cpu, true),
        CoreMetric.Kernel => new("Core kernel time", "Kernel", MetricUnit.Cpu, true),
        CoreMetric.Dpc => new("Core DPC time", "DPC", MetricUnit.Cpu, true),
        CoreMetric.Interrupt => new("Core interrupt time", "ISR", MetricUnit.Cpu, true),
        CoreMetric.InterruptRate => new("Core interrupts", "Int/s", MetricUnit.PerSecond, true),
        CoreMetric.Frequency => new("Core frequency", "Freq", MetricUnit.Megahertz, true),
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
    };
}
