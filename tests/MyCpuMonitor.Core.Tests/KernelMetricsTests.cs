using System.Diagnostics;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Native;
using MyCpuMonitor.Core.Persistence;
using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.Tests;

public class KernelMetricsTests
{
    private static RawSample Sample(double at, uint contextSwitches, uint pageFaults, uint timer100ns = 10_000,
        double performance = 120, double nominalMhz = 3000, params RawPoolTag[] tags)
    {
        var s = Raw.Sample(at, []);
        s.Kernel = new RawKernel
        {
            ContextSwitches = contextSwitches,
            SystemCalls = contextSwitches * 10,
            PageFaults = pageFaults,
            TimerResolution = timer100ns,
            PerformancePercent = performance,
            NominalMhz = nominalMhz,
        };
        s.ReadyThreads = 3;
        s.Cores[0].PerformancePercent = performance;
        s.Memory = s.Memory with { Zeroed = 100, Free = 200, Standby = 300, Modified = 400 };
        tags.CopyTo(s.PoolTags, 0);
        s.PoolTagCount = tags.Length;
        return s;
    }

    private static RawPoolTag Tag(string name, long nonPaged, uint allocs) => new()
    {
        Tag = BitConverter.ToUInt32(System.Text.Encoding.ASCII.GetBytes(name)),
        NonPagedUsed = nonPaged,
        NonPagedAllocs = allocs,
    };

    [Fact]
    public void KernelRates_FrequencyTimerAndMemoryLists()
    {
        var processor = new SampleProcessor();
        var snapshot = new Snapshot();
        processor.ProcessSystem(Sample(0, contextSwitches: uint.MaxValue - 99, pageFaults: 10), snapshot);
        processor.ProcessSystem(Sample(0.5, contextSwitches: 900, pageFaults: 60), snapshot); // Wrapped: +1000.

        var s = snapshot.System;
        Assert.Equal(2000, s.ContextSwitches, 6);
        Assert.Equal(20000, s.SystemCalls, 6);
        Assert.Equal(100, s.PageFaults, 6);
        Assert.Equal(1.0, s.TimerResolutionMs, 9);
        Assert.Equal(3600, s.FrequencyMhz, 6);
        Assert.Equal(3600, snapshot.Cores[0].FrequencyMhz, 6);
        Assert.Equal((100L, 200L, 300L, 400L), (s.MemoryZeroed, s.MemoryFree, s.MemoryStandby, s.MemoryModified));
        Assert.Equal(3600, s.GetValue(SystemMetric.CpuFrequency), 6);
    }

    [Fact]
    public void Frequency_IsZeroWhenPdhUnavailable()
    {
        var processor = new SampleProcessor();
        var snapshot = new Snapshot();
        processor.ProcessSystem(Sample(0, 0, 0, performance: double.NaN), snapshot);
        processor.ProcessSystem(Sample(1, 0, 0, performance: double.NaN), snapshot);
        Assert.Equal(0, snapshot.System.FrequencyMhz);
        Assert.Equal(0, snapshot.Cores[0].FrequencyMhz);
    }

    [Fact]
    public void PoolTags_GrowthAndAllocationRate()
    {
        var processor = new SampleProcessor();
        var table = new ProcessTable();
        processor.ProcessScan(Sample(0, 0, 0, tags: [Tag("File", 1000, 10)]), table);
        processor.ProcessScan(Sample(2, 0, 0, tags: [Tag("File", 5000, 30), Tag("New ", 64, 1)]), table);

        var tags = table.PoolTagSpan.ToArray();
        var file = tags.Single(t => t.Tag == "File");
        Assert.Equal(5000, file.NonPagedBytes);
        Assert.Equal(2000, file.NonPagedGrowth, 6);
        Assert.Equal(10, file.AllocationRate, 6);
        Assert.Equal(30, file.Outstanding);
        var fresh = tags.Single(t => t.Tag == "New ");
        Assert.Equal(0, fresh.NonPagedGrowth); // No previous value: no growth claimed.
    }

    [Theory]
    [InlineData(0x656C6946u, "File")]
    [InlineData(0x00000041u, "A...")]
    public void TagName_DecodesLittleEndianAscii(uint tag, string expected) => Assert.Equal(expected, SampleProcessor.TagName(tag));

    [Fact]
    public void PoolTagQuery_SortsAndTakesTop()
    {
        var table = new ProcessTable { PoolTagCount = 3 };
        table.PoolTags[0] = new PoolTagStats { Tag = "A", NonPagedBytes = 10, PagedGrowth = 5 };
        table.PoolTags[1] = new PoolTagStats { Tag = "B", NonPagedBytes = 30, PagedGrowth = -1 };
        table.PoolTags[2] = new PoolTagStats { Tag = "C", NonPagedBytes = 20, PagedGrowth = 9 };
        var query = new PoolTagQuery();

        Assert.Equal(["B", "C"], query.Run(table, PoolTagField.NonPagedBytes, true, 2).ToArray().Select(t => t.Tag));
        Assert.Equal(["B", "A", "C"], query.Run(table, PoolTagField.PagedGrowth, false, 10).ToArray().Select(t => t.Tag));
    }

    [Fact]
    public void NewWidgets_RoundTripAndCollectPatterns()
    {
        var layout = new LayoutDocument
        {
            Name = "k",
            Widgets =
            [
                new PoolTagTableWidgetConfig { SortBy = PoolTagField.NonPagedGrowth, TopCount = 7 },
                new ValueListWidgetConfig { Items = [new SystemSeries(SystemMetric.TimerResolution), new ProcessSeries(ProcessField.Cpu, "dwm")] },
            ],
        };
        var back = LayoutStore.Deserialize(LayoutStore.Serialize(layout));
        var pool = Assert.IsType<PoolTagTableWidgetConfig>(back.Widgets[0]);
        Assert.Equal((PoolTagField.NonPagedGrowth, 7), (pool.SortBy, pool.TopCount));
        Assert.Equal(2, Assert.IsType<ValueListWidgetConfig>(back.Widgets[1]).Items.Count);
        Assert.Equal(["dwm"], LayoutPatterns.Watched(back));
    }

    [Fact]
    public void QueueLength_ComesFromTheScanReadyThreads()
    {
        var probe = new FakeProbe(1,
            () => { var s = Raw.Sample(0, [Raw.Process(1, "a.exe")]); s.ReadyThreads = 2; return s; },
            () => { var s = Raw.Sample(1, [Raw.Process(1, "a.exe")]); s.ReadyThreads = 5; return s; });
        using var engine = new SamplingEngine(probe, new SamplingOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)));
        engine.SampleOnce();
        engine.SampleOnce();
        Assert.Equal(5, engine.Read(0, static (_, s, _) => s.System.ProcessorQueueLength));
    }
}

/// <summary>Integration: undocumented structure offsets cross-checked against independent sources (needs elevation for memory lists).</summary>
public class KernelProbeIntegrationTests
{
    [Fact]
    public void KernelCounters_MatchIndependentSources()
    {
        using var probe = new NativeSystemProbe();
        var sample = new RawSample();
        probe.CollectSystem(sample);
        var first = sample.Kernel;
        Thread.Sleep(500);
        probe.CollectSystem(sample);
        var second = sample.Kernel;

        // Context switches and system calls advance; plausible magnitudes over 0.5 s.
        Assert.InRange(unchecked(second.ContextSwitches - first.ContextSwitches), 100u, 50_000_000u);
        Assert.True(unchecked(second.SystemCalls - first.SystemCalls) > unchecked(second.ContextSwitches - first.ContextSwitches) / 10);
        // Timer resolution between 0.5 ms and 15.625 ms.
        Assert.InRange(second.TimerResolution, 5_000u, 156_250u);

        if (Environment.IsPrivilegedProcess)
        {
            // Memory lists: zeroed + free + standby ≈ available (other pages move in between: 5% tolerance).
            var m = sample.Memory;
            var lists = m.Zeroed + m.Free + m.Standby;
            Assert.InRange(lists, m.PhysicalAvailable * 0.95, m.PhysicalAvailable * 1.05);
        }
    }

    [Fact]
    public void PerformanceCounters_GiveFrequenciesAfterTwoCollections()
    {
        using var probe = new NativeSystemProbe();
        var sample = new RawSample();
        probe.CollectSystem(sample);
        Thread.Sleep(300);
        probe.CollectSystem(sample);

        Assert.InRange(sample.Kernel.NominalMhz, 500, 10_000);
        Assert.InRange(sample.Kernel.PerformancePercent, 1, 300);
        // CPUs that stayed idle have no hits yet (NaN until first activity); active ones are in range.
        var known = sample.Cores.Where(c => double.IsFinite(c.PerformancePercent)).ToArray();
        Assert.NotEmpty(known);
        Assert.All(known, c => Assert.InRange(c.PerformancePercent, 1, 300));
    }

    [Fact]
    public void SystemCollection_StaysCheap()
    {
        using var probe = new NativeSystemProbe();
        var sample = new RawSample();
        probe.CollectSystem(sample);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
            probe.CollectSystem(sample);
        Assert.True(sw.Elapsed.TotalMilliseconds / 20 < 3, $"CollectSystem took {sw.Elapsed.TotalMilliseconds / 20:0.00} ms");
    }

    [Fact]
    public void PoolTags_AndReadyThreads_AreReadWithTheScan()
    {
        using var probe = new NativeSystemProbe();
        var sample = new RawSample();
        probe.CollectProcesses(sample);
        Assert.InRange(sample.ReadyThreads, 0, 100_000);
        Assert.True(sample.PoolTagCount > 100);
        Assert.Contains(sample.PoolTags.AsSpan(0, sample.PoolTagCount).ToArray(), t => SampleProcessor.TagName(t.Tag) == "File");
    }
}
