using NockMon.Core.Layout;
using NockMon.Core.Metrics;
using NockMon.Core.Sampling;
using NockMon.Core.Settings;

namespace NockMon.Core.Tests;

public class SamplingEngineTests
{
    private static readonly SamplingOptions Options = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1));

    /// <summary>a.exe burns 0.5 CPU every second; one CPU, half busy.</summary>
    private static FakeProbe Probe(int steps = 10) => new(1,
        Enumerable.Range(0, steps).Select(i => (Func<RawSample>)(() =>
            Raw.Sample(i, [Raw.Process(10, "a.exe", cpuSeconds: 0.5 * i)], [Raw.Core(0.5 * i, 0.5 * i)]))).ToArray());

    [Fact]
    public void ScansEveryNSamples_AfterTheTableHasRates()
    {
        var probe = Probe();
        using var engine = new SamplingEngine(probe, Options);

        Assert.False(engine.SampleOnce()); // t0: baselines (scan 1).
        Assert.True(engine.SampleOnce());  // t1: scan 2, table gets rates.
        Assert.Equal(2, probe.ScanCount);
        engine.SampleOnce();               // t2: no scan.
        Assert.Equal(2, probe.ScanCount);
        engine.SampleOnce();               // t3: scan.
        Assert.Equal(3, probe.ScanCount);

        var (scanIndex, cpu, samples) = engine.Read(0, static (_, s, h) => (s.Table.ScanIndex, s.Processes[0].Cpu, h.TotalSamples));
        Assert.Equal(1, scanIndex);
        Assert.Equal(0.5, cpu, 6);
        Assert.Equal(3, samples);
    }

    [Fact]
    public void SystemValues_AtEverySample()
    {
        using var engine = new SamplingEngine(Probe(), Options);
        for (var i = 0; i < 4; i++)
            engine.SampleOnce();

        var cpu = engine.Read(0, static (_, _, h) =>
        {
            var b = new float[10];
            return b[..h.Read(new SystemSeries(SystemMetric.Cpu), b)];
        });
        Assert.Equal([0.5f, 0.5f, 0.5f], cpu);
    }

    [Fact]
    public void WatchedPatterns_SampleProcessesAtTheFastRate()
    {
        var probe = Probe();
        using var engine = new SamplingEngine(probe, Options);
        engine.SetWatchedPatterns(["a"]);
        for (var i = 0; i < 5; i++)
            engine.SampleOnce();

        Assert.Equal(1, engine.WatchedProcessCount);
        Assert.Contains(10, probe.OpenHandles);
        var (watched, cpu) = engine.Read(0, static (_, _, h) =>
        {
            var b = new float[10];
            return (h.IsWatched("a"), b[..h.Read(new ProcessSeries(ProcessField.Cpu, "a"), b)]);
        });
        Assert.True(watched);
        Assert.All(cpu, v => Assert.Equal(0.5f, v, 3));

        engine.SetWatchedPatterns([]);
        engine.SampleOnce();
        Assert.Empty(probe.OpenHandles);
    }

    [Fact]
    public void Configure_ResizesAndClearsHistory()
    {
        using var engine = new SamplingEngine(Probe(), Options);
        engine.SampleOnce();
        engine.SampleOnce();
        engine.Configure(Options with { Interval = TimeSpan.FromMilliseconds(500), ScanInterval = TimeSpan.FromSeconds(5) });

        Assert.Equal((120, 12, 0L), engine.Read(0, static (_, _, h) => (h.Capacity, h.ScanCapacity, h.TotalSamples)));
        Assert.Equal(10, engine.Options.ScanEvery);
    }

    [Fact]
    public void InvalidOptions_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SamplingEngine(Probe(), Options with { Interval = TimeSpan.FromMilliseconds(10) }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SamplingEngine(Probe(), Options with { ScanInterval = TimeSpan.FromMilliseconds(500) }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SamplingEngine(Probe(), Options with { Retention = TimeSpan.FromSeconds(3) }));
    }

    [Theory]
    [InlineData(500, 2000, 4)]
    [InlineData(1000, 1000, 1)]
    [InlineData(250, 1000, 4)]
    [InlineData(2000, 5000, 3)] // 2.5 rounds up.
    [InlineData(300, 1000, 4)]
    public void ScanEvery_RoundsToWholeSamples(int intervalMs, int scanMs, int expected) =>
        Assert.Equal(expected, new SamplingOptions(TimeSpan.FromMilliseconds(intervalMs), TimeSpan.FromMilliseconds(scanMs), TimeSpan.FromHours(1)).ScanEvery);

    [Fact]
    public void Settings_ClampToValidOptions()
    {
        var o = new AppSettings { SampleIntervalMs = 10, ScanIntervalMs = 50, HistoryMinutes = 0 }.SamplingOptions;
        o.Validate();
        Assert.Equal(SamplingOptions.MinInterval, o.Interval);
        Assert.Equal(o.Interval, o.ScanInterval);
    }

    [Fact]
    public void LayoutPatterns_CollectsProcessSeriesOfChartsAndValues()
    {
        var layout = LayoutDocument.CreateDefault();
        layout.Widgets.Add(new ValueWidgetConfig { Source = new ProcessSeries(ProcessField.Cpu, "devenv") });
        layout.Widgets.Add(new ChartWidgetConfig
        {
            Series =
            [
                new ChartSeriesConfig { Source = new ProcessSeries(ProcessField.WorkingSet, "chrome*") },
                new ChartSeriesConfig { Source = new ProcessSeries(ProcessField.Cpu, "devenv") },
                new ChartSeriesConfig { Source = new SystemSeries(SystemMetric.Cpu) },
            ],
        });
        Assert.Equal(["devenv", "chrome*"], LayoutPatterns.Watched(layout));
    }
}
