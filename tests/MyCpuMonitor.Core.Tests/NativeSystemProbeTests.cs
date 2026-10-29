using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Native;
using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.Tests;

/// <summary>Tests measuring this process's own CPU: they must not share the process with other running tests.</summary>
[CollectionDefinition(DisableParallelization = true)]
public sealed class CpuMeasurementCollection;

/// <summary>Integration tests against the real OS.</summary>
[Collection(typeof(CpuMeasurementCollection))]
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
        probe.CollectSystem(sample);
        probe.CollectProcesses(sample);

        Assert.True(sample.ProcessesElapsed >= sample.Elapsed);
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
        for (var i = 0; i < 2; i++)
        {
            probe.CollectSystem(sample);
            probe.CollectProcesses(sample);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        probe.CollectSystem(sample);
        probe.CollectProcesses(sample);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Only names of processes started between the two calls may allocate.
        Assert.True(allocated < 4096, $"Allocated {allocated} bytes");
    }

    [Fact]
    public void ProcessHandle_ReadsOwnCounters_AndRejectsWrongIdentity()
    {
        using var probe = new NativeSystemProbe();
        var sample = new RawSample();
        probe.CollectProcesses(sample);
        var self = sample.Processes.AsSpan(0, sample.ProcessCount).ToArray().Single(p => p.Pid == Environment.ProcessId);

        Assert.False(probe.TryOpenProcess(new Model.ProcessKey(self.Pid, self.CreateTime + 1), out _));
        Assert.True(probe.TryOpenProcess(new Model.ProcessKey(self.Pid, self.CreateTime), out var handle));
        try
        {
            var counters = new RawProcess { Threads = 42 };
            Assert.True(probe.TryReadProcess(handle, ref counters));
            Assert.True(counters.UserTime + counters.KernelTime >= self.UserTime + self.KernelTime);
            Assert.True(counters.WorkingSet > 1 << 20);
            Assert.True(counters.PrivateBytes > 1 << 20);
            Assert.True(counters.Handles > 10);
            Assert.Equal(42, counters.Threads); // Not available through a handle: left untouched.
        }
        finally
        {
            probe.CloseProcess(handle);
        }
    }

    [Fact]
    public void ProcessImage_ResolvesOwnPath_AndRejectsWrongIdentity()
    {
        using var current = Process.GetCurrentProcess();
        var createTime = current.StartTime.ToUniversalTime().ToFileTimeUtc();

        Assert.Equal(Environment.ProcessPath, ProcessImage.TryGetPath(Environment.ProcessId, createTime), ignoreCase: true);
        Assert.Null(ProcessImage.TryGetPath(Environment.ProcessId, createTime + 1));
        Assert.Null(ProcessImage.TryGetPath(4, 0)); // System: no image file.
    }

    [Fact]
    public void Engine_MeasuresBusyThreadCpu_InScanAndWatched()
    {
        using var probe = new NativeSystemProbe();
        var options = new SamplingOptions(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(1));
        using var engine = new SamplingEngine(probe, options);
        engine.SetWatchedPatterns([Process.GetCurrentProcess().ProcessName]);
        engine.SampleOnce();
        engine.SampleOnce();

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 500)
            Thread.SpinWait(1000);
        Assert.True(engine.SampleOnce());

        var (cpu, watchedCpu, system, logical) = engine.Read(Environment.ProcessId, static (pid, s, h) =>
        {
            var buffer = new float[1];
            h.Read(new ProcessSeries(ProcessField.Cpu, Process.GetCurrentProcess().ProcessName), buffer);
            foreach (ref readonly var p in s.ProcessSpan)
            {
                if (p.Pid == pid)
                    return (p.Cpu, buffer[0], s.System.Cpu, s.LogicalProcessors);
            }
            return (-1, 0, 0, 0);
        });

        // One spinning thread ≈ 1 CPU (scheduler noise and 15.6 ms CPU-time granularity tolerated).
        Assert.Equal(1, engine.WatchedProcessCount);
        Assert.InRange(cpu, 0.7, 1.5);
        Assert.InRange(watchedCpu, 0.7, 1.5);
        Assert.InRange(system, 0.7, logical);
    }
}
