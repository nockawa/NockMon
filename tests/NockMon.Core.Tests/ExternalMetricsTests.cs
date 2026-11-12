using NockMon.Core.Agents;
using NockMon.Core.History;
using NockMon.Core.Layout;
using NockMon.Core.Metrics;
using NockMon.Core.Persistence;
using NockMon.Core.Sampling;

namespace NockMon.Core.Tests;

/// <summary>A plugin's metric source: a fixed list, values set by the test, optionally failing.</summary>
internal sealed class FakeSource(string source, params ExternalMetric[] metrics) : IExternalMetricSource
{
    public string Plugin => "Acme.Sensors";
    public string Source => source;
    public string DisplayName => "Acme sensors";
    public IReadOnlyList<ExternalMetric> Metrics { get; set; } = metrics;
    public double[] Values { get; set; } = new double[metrics.Length];
    public bool Throws { get; set; }

    public bool TryRead(IReadOnlyList<ExternalMetric> current, Span<double> values)
    {
        if (Throws)
            throw new InvalidOperationException("Plugin bug.");
        if (!ReferenceEquals(current, Metrics))
            return false;
        Values.AsSpan(0, Math.Min(Values.Length, values.Length)).CopyTo(values);
        return true;
    }
}

/// <summary>Uses the global <see cref="ExternalMetricCatalog"/>: not in parallel with itself.</summary>
[Collection(nameof(ExternalMetricsTests))]
[CollectionDefinition(nameof(ExternalMetricsTests), DisableParallelization = true)]
public class ExternalMetricsTests : IDisposable
{
    private static readonly ExternalMetric Cpu = new("/cpu/temp/0", "CPU Package", "Package", MetricUnit.Celsius, "Ryzen");
    private static readonly ExternalMetric Fan = new("/lpc/fan/1", "Fan #1", "Fan #1", MetricUnit.Rpm, "Nuvoton");

    public ExternalMetricsTests() => ExternalMetricCatalog.Clear();

    public void Dispose() => ExternalMetricCatalog.Clear();

    private static Snapshot Sample() => new() { Cores = [new CoreStats()] };

    private static float[] Read(HistoryStore h, SeriesRef series, int count)
    {
        var buffer = new float[count];
        return buffer[..h.Read(series, buffer)];
    }

    [Fact]
    public void History_StartsAtTheFirstValue_HoldsMissingOnes_AndDropsAMetricGoneForTheRetention()
    {
        var h = new HistoryStore(capacity: 4, scanCapacity: 4, coreCount: 1);
        var source = new FakeSource("sensors", Cpu, Fan);
        var cpu = new PluginSeries("acme.sensors", "SENSORS", Cpu.Id); // Plugin and source ids ignore case.
        void Tick(double cpuValue, double fanValue, bool valid = true)
        {
            source.Values = [cpuValue, fanValue];
            h.AppendSample(Sample(), [], null, [new ExternalReading(source, source.Metrics, source.Values, valid)]);
        }

        Tick(double.NaN, 900); // No CPU value yet: its history has not started.
        Assert.Empty(Read(h, cpu, 4));
        Tick(50, 900);
        Tick(double.NaN, 910); // Held.
        Tick(52, 0, valid: false); // The read failed: both held.
        Assert.Equal([50f, 50f, 50f], Read(h, cpu, 4)); // Partial: three samples since its first value.
        Assert.Equal([900f, 900f, 910f, 910f], Read(h, new PluginSeries("Acme.Sensors", "sensors", Fan.Id), 4));

        for (var i = 0; i < 5; i++)
            h.AppendSample(Sample(), [], null, []); // The source is gone: held, then dropped after the retention.
        Assert.Empty(Read(h, cpu, 4));
    }

    [Fact]
    public void Engine_RecordsSources_SkipsAFailingOne_AndRegistersTheirMetrics()
    {
        var probe = new FakeProbe(1, Enumerable.Range(0, 6).Select(i => (Func<RawSample>)(() =>
            Raw.Sample(i, [Raw.Process(10, "a.exe", cpuSeconds: 0.1 * i)], [Raw.Core(0.5 * i, 0.5 * i)]))).ToArray());
        using var engine = new SamplingEngine(probe, new SamplingOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1)));
        var good = new FakeSource("sensors", Cpu) { Values = [61] };
        var bad = new FakeSource("broken", Fan) { Throws = true };
        engine.SetExternalSources([bad, good]);

        engine.SampleOnce();
        engine.SampleOnce();
        engine.SampleOnce();

        var values = engine.Read(0, static (_, _, h) =>
        {
            var buffer = new float[8];
            return buffer[..h.Read(new PluginSeries("Acme.Sensors", "sensors", "/cpu/temp/0"), buffer)];
        });
        Assert.NotEmpty(values);
        Assert.All(values, v => Assert.Equal(61f, v));
        Assert.Equal(MetricUnit.Celsius, new PluginSeries("Acme.Sensors", "sensors", Cpu.Id).Unit);
        Assert.Equal("CPU Package", new PluginSeries("Acme.Sensors", "sensors", Cpu.Id).DefaultLabel);
        Assert.Equal(2, ExternalMetricCatalog.Sources.Count); // The failing one registered its list before throwing.
    }

    [Fact]
    public void PluginSeries_RoundTripsInLayouts_AndKnowsWhenThePluginIsMissing()
    {
        var layout = LayoutDocument.CreateDefault("Sensors");
        var series = new PluginSeries("Acme.Sensors", "sensors", "/cpu/temp/0");
        layout.Widgets.Add(new ChartWidgetConfig { Bounds = new GridRect(0, 8, 4, 2), Series = [new ChartSeriesConfig { Source = series }] });

        var json = LayoutStore.Serialize(layout);
        Assert.Contains("\"kind\": \"plugin\"", json);
        var back = Assert.IsType<ChartWidgetConfig>(LayoutStore.Deserialize(json).Widgets[^1]);
        Assert.Equal(series, back.Series[0].Source);
        Assert.Contains("not loaded", series.Info.DisplayName); // Nothing registered: the layout still reads.
    }

    [Fact]
    public void AgentIds_ListParseAndDescribePluginMetrics()
    {
        ExternalMetricCatalog.Register(new FakeSource("sensors", Cpu), [Cpu]);

        var id = MetricIds.PluginId("Acme.Sensors", "sensors", Cpu.Id);
        Assert.Equal("plugin.Acme.Sensors/sensors//cpu/temp/0", id); // The metric id keeps its own slashes.
        Assert.Contains(MetricIds.All(), e => e.Id == id && e.Unit == "°C" && e.History);
        Assert.Equal(new PluginSeries("Acme.Sensors", "sensors", Cpu.Id), MetricIds.ToSeries(id, null, [], 1));
        Assert.Equal("CPU Package", MetricIds.Describe(id).Title);
        Assert.Throws<AgentException>(() => MetricIds.ToSeries("plugin.Acme.Sensors/sensors//nope", null, [], 1));
        Assert.Throws<AgentException>(() => MetricIds.ToSeries("plugin.Acme.Sensors", null, [], 1));
    }

    [Fact]
    public void Formatter_VoltsAndAmperes()
    {
        Assert.Equal(1.25.ToString("0.000") + " V", MetricFormatter.Format(1.25, MetricUnit.Volts, CpuScale.Machine, 1));
        Assert.Equal(12.ToString("0.0") + " V", MetricFormatter.Format(12, MetricUnit.Volts, CpuScale.Machine, 1));
        Assert.Equal(3.5.ToString("0.00") + " A", MetricFormatter.Format(3.5, MetricUnit.Amperes, CpuScale.Machine, 1));
    }
}

public class DriveTemperatureTests
{
    [Fact]
    public void ParseTemperatures_ReadsEachSensor()
    {
        var data = new byte[24 + 2 * 16];
        BitConverter.TryWriteBytes(data.AsSpan(12), (ushort)2); // InfoCount.
        BitConverter.TryWriteBytes(data.AsSpan(24 + 2), (short)49);
        BitConverter.TryWriteBytes(data.AsSpan(24 + 16 + 2), (short)-3);

        Assert.Equal([49.0, -3.0], DriveTemperatureSource.ParseTemperatures(data));
        Assert.Null(DriveTemperatureSource.ParseTemperatures(data.AsSpan(0, 30))); // Truncated.
        Assert.Null(DriveTemperatureSource.ParseTemperatures(new byte[24]));        // No sensor.
    }

    [Fact]
    public void ParseDevice_JoinsVendorAndProduct_AndReadsTheSerial()
    {
        var data = new byte[80];
        void Put(int field, int offset, string text)
        {
            BitConverter.TryWriteBytes(data.AsSpan(field), offset);
            System.Text.Encoding.ASCII.GetBytes(text).CopyTo(data, offset);
        }
        Put(12, 40, "WDC ");
        Put(16, 48, "WD_BLACK SN850X");
        Put(24, 66, " E823_8FA6 ");

        Assert.Equal(("WDC WD_BLACK SN850X", "E823_8FA6"), DriveTemperatureSource.ParseDevice(data));
    }

    [Fact]
    public void LiveDrives_ReportPlausibleTemperatures()
    {
        using var source = new DriveTemperatureSource();
        source.Start();
        for (var i = 0; i < 50 && source.Metrics.Count == 0; i++)
            Thread.Sleep(100);
        if (source.Metrics.Count == 0)
            Assert.Skip("No drive reports a temperature here (virtual disks).");
        var values = new double[source.Metrics.Count];
        Assert.True(source.TryRead(source.Metrics, values));
        Assert.All(source.Metrics, m => Assert.Equal(MetricUnit.Celsius, m.Unit));
        Assert.Contains(values, v => v is > 0 and < 100);
    }

    [Fact]
    public void NetworkAdapters_Filter()
    {
        Assert.True(NetworkAdapterSource.IsPublished(6, 1, 1));    // Ethernet, hardware, up.
        Assert.True(NetworkAdapterSource.IsPublished(6, 0, 1));    // vEthernet: virtual, up.
        Assert.False(NetworkAdapterSource.IsPublished(6, 1, 2));   // Down.
        Assert.False(NetworkAdapterSource.IsPublished(6, 3, 1));   // Filter layer.
        Assert.False(NetworkAdapterSource.IsPublished(24, 0, 1));  // Loopback.
        Assert.False(NetworkAdapterSource.IsPublished(131, 0, 1)); // Tunnel.
    }

    /// <summary>MIB_IF_ROW2 offsets, checked against .NET's own reading of the same counters.</summary>
    [Fact]
    public unsafe void NetworkAdapters_RowLayoutMatchesNetworkInterface()
    {
        var expected = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .ToDictionary(n => Guid.Parse(n.Id), n => (n.Name, n.GetIPStatistics().BytesReceived));
        Assert.Equal(0u, NetworkAdapterSource.GetIfTable2(out var table));
        try
        {
            var rows = (byte*)table + 8;
            var matched = 0;
            for (var i = 0; i < *(uint*)table; i++)
            {
                var row = rows + i * (long)NetworkAdapterSource.RowSize;
                if (!expected.TryGetValue(*(Guid*)(row + NetworkAdapterSource.GuidOffset), out var e))
                    continue;
                Assert.Equal(e.Name, new string((char*)(row + NetworkAdapterSource.AliasOffset)));
                Assert.Equal(1u, *(uint*)(row + NetworkAdapterSource.OperStatusOffset));
                // Read a moment apart: within 100 MB of each other.
                Assert.InRange((double)*(ulong*)(row + NetworkAdapterSource.InOctetsOffset), e.BytesReceived - 1e8, e.BytesReceived + 1e8);
                matched++;
            }
            Assert.True(matched > 0);
        }
        finally
        {
            NetworkAdapterSource.FreeMibTable(table);
        }
    }

    [Fact]
    public void LiveNetworkAdapters_PublishReceiveAndSend()
    {
        using var source = new NetworkAdapterSource();
        source.Start();
        for (var i = 0; i < 50 && source.Metrics.Count == 0; i++)
            Thread.Sleep(100);
        Assert.NotEmpty(source.Metrics);
        Assert.All(source.Metrics, m => Assert.Equal(MetricUnit.BytesPerSecond, m.Unit));
        Thread.Sleep(2500); // Two reads: rates.
        var values = new double[source.Metrics.Count];
        Assert.True(source.TryRead(source.Metrics, values));
        Assert.All(values, v => Assert.True(v >= 0));
    }
}
