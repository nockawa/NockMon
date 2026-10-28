using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.Tests;

/// <summary>Builds raw samples. Times are in seconds, converted to 100 ns units.</summary>
internal static class Raw
{
    public const long TicksPerSecond = TimeSpan.TicksPerSecond;

    public static RawProcess Process(int pid, string name, double cpuSeconds = 0, long createTime = 1000, long readOps = 0,
        long writeOps = 0, long workingSet = 0, long privateBytes = 0, int handles = 0, double kernelShare = 0) => new()
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
    };

    /// <summary>Core with cumulative busy/idle seconds (busy split as user unless specified).</summary>
    public static RawCore Core(double busySeconds, double idleSeconds, double kernelBusySeconds = 0, double dpcSeconds = 0) => new()
    {
        IdleTime = (long)(idleSeconds * TicksPerSecond),
        KernelTime = (long)((idleSeconds + kernelBusySeconds) * TicksPerSecond),
        UserTime = (long)((busySeconds - kernelBusySeconds) * TicksPerSecond),
        DpcTime = (long)(dpcSeconds * TicksPerSecond),
    };

    public static RawSample Sample(double atSeconds, RawProcess[] processes, RawCore[]? cores = null)
    {
        var s = new RawSample
        {
            Elapsed = TimeSpan.FromSeconds(atSeconds),
            WallTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(atSeconds),
            Cores = cores ?? [Core(0, atSeconds)],
        };
        s.EnsureProcessCapacity(processes.Length);
        processes.CopyTo(s.Processes, 0);
        s.ProcessCount = processes.Length;
        return s;
    }
}

/// <summary>Replays a sequence of raw samples.</summary>
internal sealed class FakeProbe(int cores, params Func<RawSample>[] samples) : ISystemProbe
{
    private int _next;

    public int LogicalProcessorCount => cores;

    public void Collect(RawSample sample)
    {
        var src = samples[Math.Min(_next++, samples.Length - 1)]();
        sample.Elapsed = src.Elapsed;
        sample.WallTimeUtc = src.WallTimeUtc;
        sample.EnsureProcessCapacity(src.ProcessCount);
        Array.Copy(src.Processes, sample.Processes, src.ProcessCount);
        sample.ProcessCount = src.ProcessCount;
        sample.EnsureCoreCount(src.Cores.Length);
        src.Cores.CopyTo(sample.Cores, 0);
        sample.Memory = src.Memory;
    }

    public void Dispose()
    {
    }
}
