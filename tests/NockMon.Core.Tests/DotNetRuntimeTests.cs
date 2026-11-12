using NockMon.Core.Etw;
using NockMon.Core.History;
using NockMon.Core.Metrics;
using NockMon.Core.Sampling;

namespace NockMon.Core.Tests;

public class DotNetRuntimeTests
{
    [Fact]
    public void RuntimeEvents_PerProcessRates_HeapHeld_PauseCapped_StaleCleared()
    {
        var table = new ProcessTable { Count = 3, Interval = TimeSpan.FromSeconds(2) };
        table.Items[0] = new ProcessStats { Pid = 10, Name = "app.exe" };
        table.Items[1] = new ProcessStats { Pid = 20, Name = "native.exe", GcRate = 9, ManagedHeap = 9 }; // Stale: cleared.
        table.Items[2] = new ProcessStats { Pid = 30, Name = "frozen.exe" };
        var runtimes = new Dictionary<int, RuntimeTotals>
        {
            [10] = new()
            {
                Gcs = 8, Gen2Gcs = 2, PauseMs = 50, PauseMaxMs = 30, AllocatedBytes = 4_000_000, FreedBytes = 3_000_000, PromotedBytes = 600,
                Exceptions = 6, Contentions = 10, HeapBytes = 1234,
            },
            [30] = new() { PauseMs = 5000, PauseMaxMs = 5000, FreedBytes = -100 }, // Longer than the interval: ends in it.
        };

        SampleProcessor.ApplyRuntimeEvents(table, runtimes);

        var a = table.Items[0];
        Assert.Equal((4, 1, 2.5, 30), (a.GcRate, a.Gen2GcRate, a.GcPausePercent, a.GcPauseMaxMs));
        Assert.Equal((1234L, 2_000_000, 3, 5), (a.ManagedHeap, a.AllocationRate, a.ExceptionRate, a.ContentionRate));
        Assert.Equal((0, 0L), (table.Items[1].GcRate, table.Items[1].ManagedHeap));
        Assert.Equal((1_500_000, 300), (a.FreedRate, a.PromotedRate));
        Assert.Equal((100, 0), (table.Items[2].GcPausePercent, table.Items[2].FreedRate)); // Freed clamped.
        Assert.Equal(1234, a.GetValue(ProcessField.ManagedHeap));
        Assert.Equal(30, a.GetValue(ProcessField.GcPauseMax));
    }

    [Fact]
    public void HeapStats_FreedIsTheHeapBeforeMinusAfter_UnknownForTheFirstGc_SurvivedSummed()
    {
        var r = new EtwKernelMonitor.RuntimeState();
        r.AllocatedSinceGc = 999; // Before the first GC seen: no heap before, nothing freed.
        EtwKernelMonitor.OnHeapStats(ref r, heap: 1000, promoted: 50);
        Assert.Equal((0L, 50L, 1000L), (r.Totals.FreedBytes, r.Totals.PromotedBytes, r.Totals.HeapBytes));

        r.AllocatedSinceGc = 500; // 1000 + 500 before the GC, 1200 after: 300 freed.
        EtwKernelMonitor.OnHeapStats(ref r, heap: 1200, promoted: 20);
        r.AllocatedSinceGc = 0; // Ticks lag behind: a GC can seem to free less than nothing...
        EtwKernelMonitor.OnHeapStats(ref r, heap: 1300, promoted: 0);
        r.AllocatedSinceGc = 400; // ...the next one makes up for it.
        EtwKernelMonitor.OnHeapStats(ref r, heap: 1000, promoted: 0);

        Assert.Equal((300L - 100 + 700, 70L, 1000L, 0L), (r.Totals.FreedBytes, r.Totals.PromotedBytes, r.Totals.HeapBytes, r.AllocatedSinceGc));
    }

    [Fact]
    public void GroupByName_SumsRuntimeValues_ButTakesTheLongestPause()
    {
        var s = new Snapshot { Table = new ProcessTable { Count = 2 } };
        s.Processes[0] = new ProcessStats { Pid = 1, Name = "w.exe", CreateTime = 1, GcPausePercent = 2, GcPauseMaxMs = 40, GcRate = 3, ManagedHeap = 100 };
        s.Processes[1] = new ProcessStats { Pid = 2, Name = "w.exe", CreateTime = 2, GcPausePercent = 1, GcPauseMaxMs = 15, GcRate = 4, ManagedHeap = 50 };

        var row = Assert.Single(new ProcessQuery().Run(s, new ProcessQueryOptions(ProcessField.GcPauseMax, true, 10, GroupByName: true)).ToArray());

        Assert.Equal((3, 40, 7, 150L), (row.Stats.GcPausePercent, row.Stats.GcPauseMaxMs, row.Stats.GcRate, row.Stats.ManagedHeap));
    }

    [Fact]
    public void History_PatternSeries_SumsInstancesAndNames_PeakFieldsTakeTheMaximum()
    {
        var h = new HistoryStore(8, 4, 1);
        var table = new ProcessTable { Count = 3 };
        table.Items[0] = new ProcessStats { Pid = 1, Name = "svc.exe", GcPauseMaxMs = 12, GcRate = 2, ManagedHeap = 1000 };
        table.Items[1] = new ProcessStats { Pid = 2, Name = "svc.exe", GcPauseMaxMs = 30, GcRate = 1, ManagedHeap = 500 };
        table.Items[2] = new ProcessStats { Pid = 3, Name = "svc-worker.exe", GcPauseMaxMs = 20, GcRate = 5, ManagedHeap = 250 };
        h.AppendSample(new Snapshot { Cores = [new CoreStats()] }, []);
        h.AppendScan(table);

        var one = new float[1];
        h.Read(new ProcessSeries(ProcessField.GcPauseMax, "svc*"), one);
        Assert.Equal(30, one[0]);
        h.Read(new ProcessSeries(ProcessField.GcPauseMax, "svc-worker"), one);
        Assert.Equal(20, one[0]);
        h.Read(new ProcessSeries(ProcessField.Gcs, "svc*"), one);
        Assert.Equal(8, one[0]);
        h.Read(new ProcessSeries(ProcessField.ManagedHeap, "svc"), one);
        Assert.Equal(1500, one[0]);

        var aggregates = new List<NameAggregate>();
        h.AggregateNames(ProcessField.GcPauseMax, 0, 1, aggregates);
        Assert.Equal(30, aggregates.Single(a => a.Name == "svc.exe").Max);
    }

    [Fact]
    public void History_RuntimeFieldsStayAtScanResolution_EvenWhenWatched()
    {
        var h = new HistoryStore(8, 4, 1);
        var table = new ProcessTable { Count = 1 };
        table.Items[0] = new ProcessStats { Pid = 1, Name = "a.exe", ManagedHeap = 100, GcPauseMaxMs = 7 };
        var watched = new WatchedAggregate("a"); // Handles know nothing of the runtime: zeros here must not win.
        h.AppendSample(new Snapshot { Cores = [new CoreStats()] }, [watched]);
        h.AppendScan(table);
        h.AppendSample(new Snapshot { Cores = [new CoreStats()] }, [watched]);

        var buffer = new float[2];
        h.Read(new ProcessSeries(ProcessField.ManagedHeap, "a"), buffer);
        Assert.Equal([100f, 100f], buffer);
        h.Read(new ProcessSeries(ProcessField.GcPauseMax, "a"), buffer);
        Assert.Equal([7f, 7f], buffer);
    }

    [Fact]
    public void Catalog_RuntimeFieldsAreChartable_ScanOnly_OnlyTheLongestPauseIsAPeak()
    {
        var runtime = Enum.GetValues<ProcessField>().Where(MetricCatalog.IsDotNetField).ToArray();
        Assert.Equal(10, runtime.Length);
        Assert.All(runtime, f => Assert.True(MetricCatalog.Get(f).HasHistory, f.ToString()));
        Assert.All(runtime, f => Assert.Contains(f, MetricCatalog.ProcessHistoryFields));
        Assert.All(runtime, f => Assert.False(MetricCatalog.IsWatchedField(f), f.ToString()));
        Assert.Equal([ProcessField.GcPauseMax], Enum.GetValues<ProcessField>().Where(MetricCatalog.IsPeakField));
    }

    [Fact]
    public void Engine_AppliesRuntimeTotalsAtScans()
    {
        var events = new FakeEvents();
        var probe = new FakeProbe(1,
            () => Raw.Sample(0, [Raw.Process(10, "app.exe")]),
            () => Raw.Sample(1, [Raw.Process(10, "app.exe")]),
            () => Raw.Sample(2, [Raw.Process(10, "app.exe")]));
        using var engine = new SamplingEngine(probe, new SamplingOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)), events);
        engine.SampleOnce();
        events.Runtimes[10] = new RuntimeTotals { Gcs = 3, HeapBytes = 4096 };
        engine.SampleOnce();
        Assert.Equal((3, 4096L), engine.Read(0, static (_, s, _) => (s.Processes[0].GcRate, s.Processes[0].ManagedHeap)));

        engine.SampleOnce(); // No GC since: the rate drops, the heap holds.
        Assert.Equal((0, 4096L), engine.Read(0, static (_, s, _) => (s.Processes[0].GcRate, s.Processes[0].ManagedHeap)));
    }
}

/// <summary>The .NET runtime events of this very process, through a real session (requires elevation; skipped otherwise).</summary>
public class DotNetRuntimeIntegrationTests
{
    [Fact]
    public void Session_ReportsThisProcessGcsHeapAllocationsExceptionsAndContentions()
    {
        if (!Environment.IsPrivilegedProcess)
            Assert.Skip("ETW sessions need elevation.");

        using var monitor = new EtwKernelMonitor("NockMon-Kernel-Tests-DotNet");
        monitor.Start();
        Assert.Null(monitor.Status);
        Thread.Sleep(1500); // Session startup.

        var own = new RuntimeTotals();
        Churn(); // A first GC: the base of the next ones' freed bytes.
        var runtimes = new Dictionary<int, RuntimeTotals>();
        for (var i = 0; i < 20 && (own.Gen2Gcs == 0 || own.Exceptions < 5 || own.AllocatedBytes < 50 << 20 || own.FreedBytes < 16 << 20 || own.Contentions == 0); i++)
        {
            Churn();
            Thread.Sleep(250); // Real-time buffers flush about every second.
            monitor.HarvestRuntime(runtimes);
            if (runtimes.TryGetValue(Environment.ProcessId, out var t))
                own = new RuntimeTotals
                {
                    Gcs = own.Gcs + t.Gcs, Gen2Gcs = own.Gen2Gcs + t.Gen2Gcs, PauseMs = own.PauseMs + t.PauseMs,
                    PauseMaxMs = Math.Max(own.PauseMaxMs, t.PauseMaxMs), AllocatedBytes = own.AllocatedBytes + t.AllocatedBytes,
                    FreedBytes = own.FreedBytes + t.FreedBytes, PromotedBytes = own.PromotedBytes + t.PromotedBytes,
                    Exceptions = own.Exceptions + t.Exceptions, Contentions = own.Contentions + t.Contentions, HeapBytes = t.HeapBytes,
                };
        }

        Assert.True(own.Gcs >= own.Gen2Gcs && own.Gen2Gcs > 0, $"GCs {own.Gcs}, gen 2 {own.Gen2Gcs}");
        Assert.True(own.PauseMs > 0 && own.PauseMaxMs > 0 && own.PauseMaxMs <= own.PauseMs, $"Pauses {own.PauseMs} ms, max {own.PauseMaxMs} ms");
        Assert.True(own.HeapBytes > 0, "No heap size");
        Assert.True(own.AllocatedBytes >= 50 << 20, $"Allocated {own.AllocatedBytes}");
        Assert.True(own.FreedBytes >= 16 << 20, $"Freed {own.FreedBytes}"); // Each churn drops 16 MB.
        Assert.True(own.PromotedBytes > 0, "Nothing survived");
        Assert.True(own.Exceptions >= 5, $"Exceptions {own.Exceptions}");
        Assert.True(own.Contentions > 0, "No lock contention");
    }

    private static readonly object Gate = new(); // A Monitor lock: what the contention events cover on every .NET version.

    private static void Churn()
    {
        var keep = new List<byte[]>();
        for (var i = 0; i < 1000; i++)
            keep.Add(new byte[16 * 1024]); // 16 MB.
        GC.KeepAlive(keep);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        for (var i = 0; i < 5; i++)
        {
            try
            {
                throw new InvalidOperationException("nockmon-test");
            }
            catch (InvalidOperationException)
            {
            }
        }
        // Contention: one thread holds the lock while another tries to take it.
        using var held = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (Gate)
            {
                held.Set();
                Thread.Sleep(100);
            }
        });
        holder.Start();
        held.Wait();
        lock (Gate)
        {
        }
        holder.Join();
    }
}
