using MyCpuMonitor.Core.Model;
using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.Tests;

/// <summary>Builds raw samples. Times are in seconds, converted to 100 ns units.</summary>
internal static class Raw
{
    public const long TicksPerSecond = TimeSpan.TicksPerSecond;

    public static RawProcess Process(int pid, string name, double cpuSeconds = 0, long createTime = 1000, long readOps = 0,
        long writeOps = 0, long workingSet = 0, long privateBytes = 0, int handles = 0, double kernelShare = 0, int threads = 1) => new()
    {
        Pid = pid,
        Name = name,
        CreateTime = createTime,
        KernelTime = (long)(cpuSeconds * kernelShare * TicksPerSecond),
        UserTime = (long)(cpuSeconds * (1 - kernelShare) * TicksPerSecond),
        ReadOps = readOps,
        WriteOps = writeOps,
        WorkingSet = workingSet,
        PrivateBytes = privateBytes,
        Handles = handles,
        Threads = threads,
    };

    /// <summary>Core with cumulative busy/idle seconds (busy split as user unless specified).</summary>
    public static RawCore Core(double busySeconds, double idleSeconds, double kernelBusySeconds = 0, double dpcSeconds = 0) => new()
    {
        IdleTime = (long)(idleSeconds * TicksPerSecond),
        KernelTime = (long)((idleSeconds + kernelBusySeconds) * TicksPerSecond),
        UserTime = (long)((busySeconds - kernelBusySeconds) * TicksPerSecond),
        DpcTime = (long)(dpcSeconds * TicksPerSecond),
    };

    /// <summary>A sample whose system read and process scan both happened at <paramref name="atSeconds"/>.</summary>
    public static RawSample Sample(double atSeconds, RawProcess[] processes, RawCore[]? cores = null)
    {
        var s = new RawSample
        {
            Elapsed = TimeSpan.FromSeconds(atSeconds),
            ProcessesElapsed = TimeSpan.FromSeconds(atSeconds),
            WallTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(atSeconds),
            Cores = cores ?? [Core(0, atSeconds)],
        };
        s.EnsureProcessCapacity(processes.Length);
        processes.CopyTo(s.Processes, 0);
        s.ProcessCount = processes.Length;
        return s;
    }
}

/// <summary>
/// Scripted probe. Each step defines the time, cores and processes at that point; <see cref="CollectSystem"/> advances
/// to the next step. Handles are PIDs; reading a handle returns the current step's counters for that PID.
/// </summary>
internal sealed class FakeProbe(int cores, params Func<RawSample>[] steps) : ISystemProbe
{
    private int _next;
    private RawSample _current = steps[0]();

    public int LogicalProcessorCount => cores;
    public int ScanCount { get; private set; }
    public HashSet<int> OpenHandles { get; } = [];
    public HashSet<int> Exited { get; } = [];

    public TimeSpan Now => _current.Elapsed;

    public void CollectSystem(RawSample sample)
    {
        _current = steps[Math.Min(_next++, steps.Length - 1)]();
        sample.Elapsed = _current.Elapsed;
        sample.WallTimeUtc = _current.WallTimeUtc;
        sample.EnsureCoreCount(_current.Cores.Length);
        _current.Cores.CopyTo(sample.Cores, 0);
        sample.Memory = _current.Memory;
    }

    public void CollectProcesses(RawSample sample)
    {
        ScanCount++;
        sample.ProcessesElapsed = _current.ProcessesElapsed;
        sample.EnsureProcessCapacity(_current.ProcessCount);
        Array.Copy(_current.Processes, sample.Processes, _current.ProcessCount);
        sample.ProcessCount = _current.ProcessCount;
        sample.ReadyThreads = _current.ReadyThreads;
    }

    public bool TryOpenProcess(ProcessKey key, out nint handle)
    {
        handle = key.Pid;
        if (Exited.Contains(key.Pid) || !Find(key.Pid, out _))
            return false;
        OpenHandles.Add(key.Pid);
        return true;
    }

    public bool TryReadProcess(nint handle, ref RawProcess counters)
    {
        if (Exited.Contains((int)handle) || !Find((int)handle, out var p))
            return false;
        counters.UserTime = p.UserTime;
        counters.KernelTime = p.KernelTime;
        counters.WorkingSet = p.WorkingSet;
        counters.PrivateBytes = p.PrivateBytes;
        counters.ReadOps = p.ReadOps;
        counters.WriteOps = p.WriteOps;
        counters.OtherOps = p.OtherOps;
        counters.Handles = p.Handles;
        return true;
    }

    public void CloseProcess(nint handle) => OpenHandles.Remove((int)handle);

    private bool Find(int pid, out RawProcess process)
    {
        foreach (var p in _current.Processes.AsSpan(0, _current.ProcessCount))
        {
            if (p.Pid == pid)
            {
                process = p;
                return true;
            }
        }
        process = default;
        return false;
    }

    public void Dispose()
    {
    }
}
