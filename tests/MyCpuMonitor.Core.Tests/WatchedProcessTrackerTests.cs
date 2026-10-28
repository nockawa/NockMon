using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.Tests;

public class WatchedProcessTrackerTests
{
    private static readonly int Cpu = MetricCatalog.HistoryIndexOf(ProcessField.Cpu);
    private static readonly int Instances = MetricCatalog.HistoryIndexOf(ProcessField.Instances);
    private static readonly int Threads = MetricCatalog.HistoryIndexOf(ProcessField.Threads);
    private static readonly int WorkingSet = MetricCatalog.HistoryIndexOf(ProcessField.WorkingSet);

    private readonly List<RawSample> _steps = [];
    private readonly FakeProbe _probe;
    private readonly WatchedProcessTracker _tracker;
    private readonly RawSample _raw = new();

    public WatchedProcessTrackerTests()
    {
        // t: a.exe (pid 1) 1 CPU, b.exe (pid 2) 0.5 CPU, c.exe (pid 3) idle; pid 2 exits at t=3.
        for (var t = 0; t < 6; t++)
        {
            var at = t;
            RawProcess[] processes = at < 3
                ? [Raw.Process(1, "a.exe", at, workingSet: 100, threads: 4), Raw.Process(2, "b.exe", 0.5 * at, threads: 2), Raw.Process(3, "c.exe")]
                : [Raw.Process(1, "a.exe", at, workingSet: 100, threads: 4), Raw.Process(3, "c.exe")];
            _steps.Add(Raw.Sample(at, processes));
        }
        _probe = new FakeProbe(1, _steps.Select(s => (Func<RawSample>)(() => s)).ToArray());
        _tracker = new WatchedProcessTracker(_probe);
    }

    /// <summary>Advances the fake clock, scans if requested (rates computed from the previous scan), ticks.</summary>
    private void Step(bool scan, ProcessTable? rates = null)
    {
        _probe.CollectSystem(_raw);
        if (scan)
        {
            _probe.CollectProcesses(_raw);
            _tracker.OnScan(_raw, rates);
        }
        _tracker.Tick();
    }

    private float[] Values(string pattern) => _tracker.Aggregates.Single(a => a.Pattern == pattern).Values;

    [Fact]
    public void AggregatesPerPattern_AtEachTick()
    {
        _tracker.SetPatterns(["a;b", "b"]);
        Step(scan: true);  // t0: discovered (baseline scan: no first rates).
        Step(scan: false); // t1: handle rates.

        Assert.Equal(2, _tracker.WatchedCount);
        Assert.Equal(1.5f, Values("a;b")[Cpu], 3);
        Assert.Equal(2f, Values("a;b")[Instances]);
        Assert.Equal(6f, Values("a;b")[Threads]);
        Assert.Equal(100f, Values("a;b")[WorkingSet]);
        Assert.Equal(0.5f, Values("b")[Cpu], 3);
        Assert.Equal(1f, Values("b")[Instances]);
    }

    [Fact]
    public void NewProcess_FirstTickUsesScanRates()
    {
        _tracker.SetPatterns(["a"]);
        var rates = HistoryStoreTests.Table(("a.exe", 0.75));
        rates.Items[0].Pid = 1;
        rates.Items[0].CreateTime = 1000;
        Step(scan: true, rates);

        Assert.Equal(0.75f, Values("a")[Cpu], 3);
        Assert.Equal(1f, Values("a")[Instances]);
    }

    [Fact]
    public void ExitedProcess_IsRetired()
    {
        _tracker.SetPatterns(["b"]);
        Step(scan: true);
        Step(scan: false);
        Step(scan: false);
        Assert.Equal(1, _tracker.WatchedCount);

        Step(scan: false); // t3: pid 2 gone, handle read fails.
        Assert.Equal(0, _tracker.WatchedCount);
        Assert.Empty(_probe.OpenHandles);
        Assert.Equal(0f, Values("b")[Instances]);
    }

    [Fact]
    public void SetPatterns_ClosesHandles()
    {
        _tracker.SetPatterns(["*"]);
        Step(scan: true);
        Assert.Equal(3, _probe.OpenHandles.Count);

        _tracker.SetPatterns(["c"]);
        Assert.Empty(_probe.OpenHandles);
        Step(scan: true);
        Assert.Equal([3], _probe.OpenHandles);
    }
}
