using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Native;
using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.Tests;

/// <summary>Integration tests against the real OS.</summary>
public class NativeSystemProbeTests
{
    [Fact]
    public void NativeStructLayouts_MatchWindowsHeaders()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(256, Unsafe.SizeOf<SystemProcessInformation>());
        Assert.Equal(56, (int)Marshal.OffsetOf<SystemProcessInformation>(nameof(SystemProcessInformation.ImageName)));
        Assert.Equal(80, (int)Marshal.OffsetOf<SystemProcessInformation>(nameof(SystemProcessInformation.UniqueProcessId)));
        Assert.Equal(184, (int)Marshal.OffsetOf<SystemProcessInformation>(nameof(SystemProcessInformation.PagefileUsage)));
        Assert.Equal(208, (int)Marshal.OffsetOf<SystemProcessInformation>(nameof(SystemProcessInformation.ReadOperationCount)));
        Assert.Equal(48, Unsafe.SizeOf<SystemProcessorPerformanceInformation>());
    }

    [Fact]
    public void Collect_FindsCurrentProcessCoresAndMemory()
    {
        using var probe = new NativeSystemProbe();
        var sample = new RawSample();
        probe.Collect(sample);

        Assert.Equal(Environment.ProcessorCount, probe.LogicalProcessorCount);
        Assert.Equal(probe.LogicalProcessorCount, sample.Cores.Length);
        Assert.True(sample.ProcessCount > 10);

        var processes = sample.Processes.AsSpan(0, sample.ProcessCount).ToArray();
        Assert.Contains(processes, p => p.Pid == 0 && p.Name == "Idle");
        Assert.Contains(processes, p => p.Pid == 4 && p.Name == "System");

        using var current = Process.GetCurrentProcess();
        var self = Assert.Single(processes, p => p.Pid == Environment.ProcessId);
        Assert.Equal(current.ProcessName + ".exe", self.Name, ignoreCase: true);
        Assert.Equal(current.StartTime.ToUniversalTime().ToFileTimeUtc(), self.CreateTime, TimeSpan.FromMilliseconds(1).Ticks);
        Assert.InRange(self.Threads, 1, 10_000);
        Assert.True(self.WorkingSet > 1 << 20);
        Assert.True(self.PrivateBytes > 1 << 20);

        Assert.True(sample.Memory.PhysicalTotal > 1L << 30);
        Assert.InRange(sample.Memory.PhysicalAvailable, 1, sample.Memory.PhysicalTotal);
        Assert.True(sample.Memory.CommitLimit >= sample.Memory.CommitTotal);
    }

    [Fact]
    public void Collect_IsAllocationFreeInSteadyState()
    {
        using var probe = new NativeSystemProbe();
        var sample = new RawSample();
        probe.Collect(sample);
        probe.Collect(sample);

        var before = GC.GetAllocatedBytesForCurrentThread();
        probe.Collect(sample);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Only names of processes started between the two calls may allocate.
        Assert.True(allocated < 4096, $"Allocated {allocated} bytes");
    }

    [Fact]
    public void Engine_MeasuresBusyThreadCpu()
    {
        using var probe = new NativeSystemProbe();
        using var engine = new SamplingEngine(probe, TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(1));
        engine.SampleOnce();

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 500)
            Thread.SpinWait(1000);
        Assert.True(engine.SampleOnce());

        var (cpu, system, logical) = engine.Read(Environment.ProcessId, static (pid, s, _) =>
        {
            foreach (ref readonly var p in s.ProcessSpan)
            {
                if (p.Pid == pid)
                    return (p.Cpu, s.System.Cpu, s.LogicalProcessors);
            }
            return (-1, 0, 0);
        });

        // One spinning thread ≈ 1 core (scheduler noise tolerated).
        Assert.InRange(cpu, 0.7, 1.5);
        Assert.InRange(system, 0.7, logical);
        Assert.InRange(MetricFormatter.CpuPercent(cpu, logical), 70.0 / logical, 150.0 / logical);
    }
}
