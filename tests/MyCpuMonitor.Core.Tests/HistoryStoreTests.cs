using MyCpuMonitor.Core.History;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.Tests;

public class HistoryStoreTests
{
    private static readonly int Cpu = MetricCatalog.HistoryIndexOf(ProcessField.Cpu);

    private static Snapshot Sample(double systemCpu)
    {
        var s = new Snapshot { Cores = [new CoreStats { Usage = systemCpu }] };
        s.System.Cpu = systemCpu;
        return s;
    }

    internal static ProcessTable Table(params (string Name, double Cpu)[] processes)
    {
        var t = new ProcessTable { Count = processes.Length };
        for (var i = 0; i < processes.Length; i++)
            t.Items[i] = new ProcessStats { Pid = i + 1, Name = processes[i].Name, Cpu = processes[i].Cpu };
        return t;
    }

    private static WatchedAggregate Watched(string pattern, float cpu)
    {
        var a = new WatchedAggregate(pattern);
        a.Values[Cpu] = cpu;
        return a;
    }

    private static float[] Read(HistoryStore h, SeriesRef series, int count)
    {
        var buffer = new float[count];
        var n = h.Read(series, buffer);
        return buffer[..n];
    }

    /// <summary>One sample per call; a scan of <paramref name="table"/> attached when given.</summary>
    private static void Tick(HistoryStore h, ProcessTable? table = null, double systemCpu = 0, params WatchedAggregate[] watched)
    {
        h.AppendSample(Sample(systemCpu), watched);
        if (table is not null)
            h.AppendScan(table);
    }

    [Fact]
    public void Read_ReturnsLatestOldestFirst_AcrossWrap()
    {
        var h = new HistoryStore(capacity: 4, scanCapacity: 4, coreCount: 1);
        for (var i = 1; i <= 6; i++)
            Tick(h, systemCpu: i);

        Assert.Equal(6, h.TotalSamples);
        Assert.Equal(4, h.Count);
        Assert.Equal([3f, 4f, 5f, 6f], Read(h, new SystemSeries(SystemMetric.Cpu), 10));
        Assert.Equal([5f, 6f], Read(h, new SystemSeries(SystemMetric.Cpu), 2));
        Assert.Equal([5f, 6f], Read(h, new CoreSeries(CoreMetric.Usage, 0), 2));
    }

    [Fact]
    public void ProcessSeries_SumsInstancesAndMatchingNames()
    {
        var h = new HistoryStore(4, 4, 1);
        Tick(h, Table(("chrome.exe", 0.5), ("chrome.exe", 0.25), ("code.exe", 1)));
        Tick(h, Table(("chrome.exe", 1)));

        Assert.Equal([0.75f, 1f], Read(h, new ProcessSeries(ProcessField.Cpu, "chrome"), 2));
        Assert.Equal([2f, 1f], Read(h, new ProcessSeries(ProcessField.Instances, "chrome.exe"), 2));
        Assert.Equal([1.75f, 1f], Read(h, new ProcessSeries(ProcessField.Cpu, "chrome*;code*"), 2));
    }

    [Fact]
    public void ScanValues_HoldUntilNextScan_AtSampleRate()
    {
        var h = new HistoryStore(8, 4, 1);
        Tick(h);                              // Sample 0: before any scan -> 0.
        Tick(h, Table(("a.exe", 1)));         // Sample 1: scan 0.
        Tick(h);                              // Sample 2: holds scan 0.
        Tick(h);                              // Sample 3: holds scan 0.
        Tick(h, Table(("a.exe", 2)));         // Sample 4: scan 1.
        Tick(h);                              // Sample 5: holds scan 1.

        Assert.Equal([0f, 1f, 1f, 1f, 2f, 2f], Read(h, new ProcessSeries(ProcessField.Cpu, "a"), 8));
    }

    [Fact]
    public void WatchedData_OverlaysUpsampledScans_FromItsFirstSample()
    {
        var h = new HistoryStore(8, 4, 1);
        Tick(h, Table(("a.exe", 1)));                            // 0: scan only.
        Tick(h);                                                 // 1: holds 1.
        Tick(h, null, 0, Watched("a", 1.5f));                    // 2: watched from here.
        Tick(h, Table(("a.exe", 2)), 0, Watched("a", 2.5f));     // 3: watched wins over the scan.

        Assert.True(h.IsWatched("a"));
        Assert.Equal([1f, 1f, 1.5f, 2.5f], Read(h, new ProcessSeries(ProcessField.Cpu, "a"), 4));
        // Another pattern matching the same process has no fast data: scan resolution only.
        Assert.Equal([1f, 1f, 1f, 2f], Read(h, new ProcessSeries(ProcessField.Cpu, "a*"), 4));
    }

    [Fact]
    public void WatchedSeries_DroppedWhenNoLongerWatched()
    {
        var h = new HistoryStore(8, 4, 1);
        Tick(h, Table(("a.exe", 1)), 0, Watched("a", 5));
        Tick(h);

        Assert.False(h.IsWatched("a"));
        Assert.Equal([1f, 1f], Read(h, new ProcessSeries(ProcessField.Cpu, "a"), 2));
    }

    [Fact]
    public void NameSeries_EvictedAfterAFullScanWindowAbsent()
    {
        var h = new HistoryStore(16, 3, 1);
        Tick(h, Table(("a.exe", 1)));
        for (var i = 0; i < 2; i++)
            Tick(h, Table());
        Assert.Contains("a.exe", h.ProcessNames); // Still in the 3-scan window.

        Tick(h, Table());
        Assert.DoesNotContain("a.exe", h.ProcessNames);
    }

    [Fact]
    public void ProcessField_WithoutHistory_Throws()
    {
        var h = new HistoryStore(4, 4, 1);
        Assert.Throws<ArgumentException>(() => h.Read(new ProcessSeries(ProcessField.Pid, "*"), new float[4]));
    }

    [Fact]
    public void Timestamps_AlignWithValues()
    {
        var h = new HistoryStore(2, 2, 1);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++)
        {
            var s = Sample(i);
            s.WallTimeUtc = t0.AddSeconds(i);
            h.AppendSample(s, []);
        }
        var times = new DateTime[2];
        Assert.Equal(2, h.ReadTimestamps(times));
        Assert.Equal([t0.AddSeconds(1), t0.AddSeconds(2)], times);
    }

    [Fact]
    public void ScanBeforeAnySample_Throws() =>
        Assert.Throws<InvalidOperationException>(() => new HistoryStore(4, 4, 1).AppendScan(Table()));

    [Fact]
    public void CoreCountMismatch_Throws()
    {
        var h = new HistoryStore(4, 4, 2);
        Assert.Throws<ArgumentException>(() => h.AppendSample(Sample(0), []));
    }
}
