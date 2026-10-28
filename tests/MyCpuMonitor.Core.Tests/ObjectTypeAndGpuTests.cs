using System.Globalization;
using MyCpuMonitor.Core.Gpu;
using MyCpuMonitor.Core.History;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Persistence;
using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.Tests;

internal sealed class FakeGpu : IGpuSource
{
    public double Utilization = 40;
    public double[] Engines = [10, 70, double.NaN];
    public Dictionary<int, double> Processes = new() { [10] = 25 };
    public List<(string, string)> Watched = [];

    public IReadOnlyList<GpuAdapterInfo> Adapters { get; } =
        [new GpuAdapterInfo("GPU A", 1, 3, hasNvml: true) { Engines = ["3D", "Compute 0", "Copy"] }];

    public void SetWatchedEngines(IEnumerable<(string Adapter, string Pattern)> engines) => Watched = engines.ToList();

    public void Collect(List<GpuStats> target)
    {
        if (target.Count == 0)
            target.Add(new GpuStats());
        var s = target[0];
        s.Name = "GPU A";
        s.EngineNames = Adapters[0].Engines;
        s.Engines = (double[])Engines.Clone();
        Array.Fill(s.Metrics, double.NaN);
        s[GpuMetric.Utilization] = Utilization;
        s[GpuMetric.DedicatedUsed] = 1 << 30;
    }

    public void CollectProcesses(Dictionary<int, double> gpuByPid)
    {
        gpuByPid.Clear();
        foreach (var (k, v) in Processes)
            gpuByPid[k] = v;
    }

    public void Dispose()
    {
    }
}

public class ObjectTypeTests
{
    private static RawSample Sample(double at, uint tokens, uint tokenHandles)
    {
        var s = Raw.Sample(at, []);
        s.ObjectTypes[0] = new RawObjectType { Name = "Token", Objects = tokens, Handles = tokenHandles, PeakObjects = tokens };
        s.ObjectTypeCount = 1;
        return s;
    }

    [Fact]
    public void Growth_IsPerMinute()
    {
        var processor = new SampleProcessor();
        var table = new ProcessTable();
        processor.ProcessScan(Sample(0, 1000, 10), table);
        processor.ProcessScan(Sample(30, 1100, 12), table);

        var t = Assert.Single(table.ObjectTypeSpan.ToArray());
        Assert.Equal(("Token", 1100L, 12L), (t.Name, t.Objects, t.Handles));
        Assert.Equal(200, t.ObjectGrowth, 6);
        Assert.Equal(4, t.HandleGrowth, 6);
    }

    [Fact]
    public void Query_SortsByField()
    {
        var table = new ProcessTable { ObjectTypeCount = 3 };
        table.ObjectTypes[0] = new ObjectTypeStats { Name = "File", Objects = 9, ObjectGrowth = 1 };
        table.ObjectTypes[1] = new ObjectTypeStats { Name = "Token", Objects = 5, ObjectGrowth = 50 };
        table.ObjectTypes[2] = new ObjectTypeStats { Name = "Job", Objects = 1, ObjectGrowth = 3 };
        var query = new ObjectTypeQuery();
        Assert.Equal(["Token", "Job"], query.Run(table, ObjectTypeField.ObjectGrowth, true, 2).ToArray().Select(t => t.Name));
        Assert.Equal(["File", "Job", "Token"], query.Run(table, ObjectTypeField.Name, false, 5).ToArray().Select(t => t.Name));
    }

    [Fact]
    public void History_ObjectTypeSeries_AtScanResolution()
    {
        var h = new HistoryStore(8, 4, 1);
        var table = new ProcessTable { ObjectTypeCount = 1 };
        table.ObjectTypes[0] = new ObjectTypeStats { Name = "Token", Objects = 100, Handles = 3 };
        h.AppendSample(new Snapshot { Cores = [new CoreStats()] }, []);
        h.AppendScan(table);
        h.AppendSample(new Snapshot { Cores = [new CoreStats()] }, []);

        var buffer = new float[2];
        h.Read(new ObjectTypeSeries("token", ObjectTypeMetric.Objects), buffer);
        Assert.Equal([100f, 100f], buffer);
        h.Read(new ObjectTypeSeries("Token", ObjectTypeMetric.Handles), buffer);
        Assert.Equal([3f, 3f], buffer);
        Assert.Contains("Token", h.ObjectTypeNames);
    }

    [Fact]
    public void Probe_ReadsObjectTypes()
    {
        using var probe = new NativeSystemProbe();
        var sample = new RawSample();
        probe.CollectProcesses(sample);
        var types = sample.ObjectTypes.AsSpan(0, sample.ObjectTypeCount).ToArray();
        Assert.True(types.Length > 30);
        foreach (var name in new[] { "Process", "Thread", "Token", "File", "Key", "Job" })
            Assert.Contains(types, t => t.Name == name);
        var process = types.Single(t => t.Name == "Process");
        Assert.InRange(process.Objects, (uint)(sample.ProcessCount / 2), (uint)(sample.ProcessCount * 10)); // Includes zombies.
        Assert.True(types.Single(t => t.Name == "File").Objects >= types.Single(t => t.Name == "File").Handles / 2);
    }
}

public class GpuTests
{
    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x08DEF4D5_phys_0_eng_14_engtype_Security_1", 0x08DEF4D5L, 14, "Security 1")]
    [InlineData("pid_4_luid_0x00000001_0x00017370_phys_0_eng_2_engtype_Compute 0", 0x100017370L, 2, "Compute 0")]
    public void EngineNames_ParsePdhInstances(string instance, long luid, int engine, string type)
    {
        Assert.True(GpuEngineNames.TryParse(instance, out var l, out var e, out var t));
        Assert.Equal((luid, engine, type), (l, e, t));
    }

    [Fact]
    public void EngineNames_RejectGarbage() => Assert.False(GpuEngineNames.TryParse("_Total", out _, out _, out _));

    [Fact]
    public void History_EngineSeries_IsBusiestMatchingEngine_NaNAsZero()
    {
        var h = new HistoryStore(4, 4, 1);
        var gpu = new GpuStats { Name = "GPU A", EngineNames = ["3D", "Compute 0", "Compute 1"], Engines = [5, 30, 60] };
        Array.Fill(gpu.Metrics, double.NaN);
        gpu[GpuMetric.Temperature] = 55;
        h.AppendSample(new Snapshot { Cores = [new CoreStats()], Gpus = [gpu] }, []);
        gpu.Engines = [5, 80, double.NaN];
        h.AppendSample(new Snapshot { Cores = [new CoreStats()], Gpus = [gpu] }, []);

        var buffer = new float[2];
        h.Read(new GpuSeries(GpuMetric.Engine, "GPU A", "Compute*"), buffer);
        Assert.Equal([60f, 80f], buffer);
        h.Read(new GpuSeries(GpuMetric.Engine, "gpu a", "3D"), buffer);
        Assert.Equal([5f, 5f], buffer);
        h.Read(new GpuSeries(GpuMetric.Temperature, "GPU A"), buffer);
        Assert.Equal([55f, 55f], buffer);
        h.Read(new GpuSeries(GpuMetric.Power, "GPU A"), buffer); // Unsupported (NaN) reads 0.
        Assert.Equal([0f, 0f], buffer);
        h.Read(new GpuSeries(GpuMetric.Temperature, "Missing GPU"), buffer);
        Assert.Equal([0f, 0f], buffer);
    }

    [Fact]
    public void LayoutPatterns_GpuEnginesToPoll()
    {
        GpuAdapterInfo[] adapters = [new("NV", 1, 3, hasNvml: true), new("AMD", 2, 3, hasNvml: false)];
        var layout = new LayoutDocument
        {
            Name = "g",
            Widgets =
            [
                new ChartWidgetConfig
                {
                    Series =
                    [
                        new ChartSeriesConfig { Source = new GpuSeries(GpuMetric.Engine, "NV", "Video*") },
                        new ChartSeriesConfig { Source = new GpuSeries(GpuMetric.Utilization, "NV") },  // NVML: no engines.
                        new ChartSeriesConfig { Source = new GpuSeries(GpuMetric.Utilization, "AMD") }, // Busiest engine: all.
                    ],
                },
                new GpuEnginesWidgetConfig { EnginePattern = "Compute*" }, // First adapter.
            ],
        };
        Assert.Equal(new HashSet<(string, string)> { ("NV", "Video*"), ("AMD", "*"), ("NV", "Compute*") }, LayoutPatterns.GpuEngines(layout, adapters).ToHashSet());
    }

    [Fact]
    public void Engine_CollectsGpuPerSampleAndPerProcessAtScans()
    {
        var gpu = new FakeGpu();
        var probe = new FakeProbe(1,
            () => Raw.Sample(0, [Raw.Process(10, "game.exe")]),
            () => Raw.Sample(1, [Raw.Process(10, "game.exe")]));
        using var engine = new SamplingEngine(probe, new SamplingOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)), gpu: gpu);
        engine.SetWatchedGpuEngines([("GPU A", "Compute*")]);
        engine.SampleOnce();
        engine.SampleOnce();

        Assert.Equal([("GPU A", "Compute*")], gpu.Watched);
        var (utilization, processGpu, history) = engine.Read(0, static (_, s, h) =>
        {
            var buffer = new float[1];
            h.Read(new ProcessSeries(ProcessField.Gpu, "game"), buffer);
            return (s.Gpus[0][GpuMetric.Utilization], s.Processes[0].GpuPercent, buffer[0]);
        });
        Assert.Equal((40, 25, 25f), (utilization, processGpu, history));
    }

    [Fact]
    public void Units_Format()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Assert.Equal("42 %", MetricFormatter.Format(42, MetricUnit.Percent, CpuScale.Machine, 1));
        Assert.Equal("4.5 %", MetricFormatter.Format(4.5, MetricUnit.Percent, CpuScale.Machine, 1));
        Assert.Equal("37 °C", MetricFormatter.Format(36.8, MetricUnit.Celsius, CpuScale.Machine, 1));
        Assert.Equal("13.5 W", MetricFormatter.Format(13.5, MetricUnit.Watts, CpuScale.Machine, 1));
        Assert.Equal("1,200 rpm", MetricFormatter.Format(1200, MetricUnit.Rpm, CpuScale.Machine, 1));
    }

    [Fact]
    public void Series_RoundTripAndLabels()
    {
        var layout = new LayoutDocument
        {
            Name = "g",
            Widgets =
            [
                new ValueListWidgetConfig { Items = [new GpuSeries(GpuMetric.Engine, "NVIDIA GeForce RTX 4070 Ti", "Compute*"), new ObjectTypeSeries("Token", ObjectTypeMetric.Objects)] },
                new GpuEnginesWidgetConfig { Adapter = "AMD Radeon(TM) Graphics", EnginePattern = "Video*", Columns = 3 },
            ],
        };
        var back = LayoutStore.Deserialize(LayoutStore.Serialize(layout));
        var items = Assert.IsType<ValueListWidgetConfig>(back.Widgets[0]).Items;
        Assert.Equal(new GpuSeries(GpuMetric.Engine, "NVIDIA GeForce RTX 4070 Ti", "Compute*"), items[0]);
        Assert.Equal("RTX 4070 Ti Compute*", items[0].DefaultLabel);
        Assert.Equal("Token objects", items[1].DefaultLabel);
        Assert.Equal(("AMD Radeon(TM) Graphics", "Video*", 3), (((GpuEnginesWidgetConfig)back.Widgets[1]).Adapter, ((GpuEnginesWidgetConfig)back.Widgets[1]).EnginePattern, ((GpuEnginesWidgetConfig)back.Widgets[1]).Columns));
    }
}

/// <summary>Integration with the machine's GPUs (skipped where absent).</summary>
public class GpuProbeIntegrationTests
{
    [Fact]
    public void Probe_ReadsAdaptersMemoryAndWatchedEngines()
    {
        using var gpu = new GpuProbe();
        if (gpu.Adapters.Count == 0)
            Assert.Skip("No GPU adapter.");
        var stats = new List<GpuStats>();
        var first = gpu.Adapters[0];
        gpu.SetWatchedEngines([(first.Name, "*")]);
        gpu.Collect(stats);
        Thread.Sleep(300);
        gpu.Collect(stats);

        Assert.Equal(gpu.Adapters.Count, stats.Count);
        var s = stats[0];
        Assert.True(s[GpuMetric.DedicatedTotal] > 0 || s[GpuMetric.SharedTotal] > 0);
        Assert.All(s.Engines, e => Assert.InRange(e, 0, 100));

        var nvidia = stats.FirstOrDefault(x => gpu.Adapters.First(a => a.Name == x.Name).HasNvml);
        if (nvidia is not null)
        {
            Assert.InRange(nvidia[GpuMetric.Utilization], 0, 100);
            Assert.InRange(nvidia[GpuMetric.Temperature], 10, 110);
            Assert.True(nvidia[GpuMetric.Power] > 0);
        }
    }
}
