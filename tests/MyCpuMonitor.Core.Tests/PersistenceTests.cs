using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Persistence;
using MyCpuMonitor.Core.Settings;

namespace MyCpuMonitor.Core.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public void Layout_RoundTrips_PolymorphicWidgetsAndSeries()
    {
        var layout = LayoutDocument.CreateDefault("Work");
        layout.Widgets.Add(new ValueWidgetConfig
        {
            Bounds = new GridRect(0, 8, 2, 2),
            Source = new ProcessSeries(ProcessField.Cpu, "devenv"),
        });
        layout.Widgets.Add(new ChartWidgetConfig
        {
            Bounds = new GridRect(2, 8, 4, 2),
            Series = [new ChartSeriesConfig { Source = new CoreSeries(CoreMetric.Dpc, 3), Color = "#FF8800" }],
        });

        var json = LayoutStore.Serialize(layout);
        var back = LayoutStore.Deserialize(json);

        Assert.Equal(json, LayoutStore.Serialize(back));
        Assert.Contains("\"$type\": \"processTable\"", json);
        Assert.Contains("\"kind\": \"process\"", json);
        Assert.Contains("\"field\": \"Cpu\"", json);
        var value = Assert.IsType<ValueWidgetConfig>(back.Widgets[3]);
        Assert.Equal(new ProcessSeries(ProcessField.Cpu, "devenv"), value.Source);
        var chart = Assert.IsType<ChartWidgetConfig>(back.Widgets[4]);
        Assert.Equal(new CoreSeries(CoreMetric.Dpc, 3), chart.Series[0].Source);
        Assert.Equal(new GridRect(2, 8, 4, 2), chart.Bounds);
    }

    [Fact]
    public void Layout_NewerSchema_IsRejected()
    {
        var json = LayoutStore.Serialize(LayoutDocument.CreateDefault()).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");
        Assert.Throws<NotSupportedException>(() => LayoutStore.Deserialize(json));
    }

    [Fact]
    public void DefaultLayout_HasNoCollisions() => Assert.Empty(LayoutDocument.CreateDefault().FindCollisions());

    [Theory]
    [InlineData(0, 0, 2, 2, 1, 1, 2, 2, true)]
    [InlineData(0, 0, 2, 2, 2, 0, 2, 2, false)]
    [InlineData(0, 0, 2, 2, 0, 2, 2, 2, false)]
    [InlineData(0, 0, 4, 4, 1, 1, 1, 1, true)]
    public void GridRect_Intersects(int c1, int r1, int w1, int h1, int c2, int r2, int w2, int h2, bool expected)
    {
        Assert.Equal(expected, new GridRect(c1, r1, w1, h1).Intersects(new GridRect(c2, r2, w2, h2)));
        Assert.Equal(expected, new GridRect(c2, r2, w2, h2).Intersects(new GridRect(c1, r1, w1, h1)));
    }

    [Fact]
    public void Store_SaveListLoadRenameDelete()
    {
        var store = new LayoutStore(_dir);
        Assert.Empty(store.List());

        store.Save(LayoutDocument.CreateDefault("Work"));
        store.Save(LayoutDocument.CreateDefault("Gaming: low"));
        Assert.Equal(["Gaming: low", "Work"], store.List());
        Assert.True(File.Exists(Path.Combine(_dir, "Gaming_ low.json")));
        Assert.Equal("Gaming: low", store.Load("Gaming: low").Name);

        store.Rename("Work", "Office");
        Assert.Equal(["Gaming: low", "Office"], store.List());

        Assert.True(store.Delete("Office"));
        Assert.False(store.Delete("Office"));
        Assert.Throws<FileNotFoundException>(() => store.Load("Office"));
    }

    [Fact]
    public void Store_NamesSanitizingToSameFile_Conflict()
    {
        var store = new LayoutStore(_dir);
        store.Save(LayoutDocument.CreateDefault("a:b"));
        Assert.Throws<InvalidOperationException>(() => store.Save(LayoutDocument.CreateDefault("a?b")));
        store.Save(LayoutDocument.CreateDefault("A:B")); // Same name, different case: overwrite.
    }

    [Fact]
    public void Store_SkipsCorruptFiles()
    {
        var store = new LayoutStore(_dir);
        store.Save(LayoutDocument.CreateDefault("Good"));
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{ not json");
        Assert.Equal(["Good"], store.List());
    }

    [Fact]
    public void Settings_RoundTrip_AndDefaultsOnCorrupt()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path);
        Assert.Equal(new AppSettings().SampleIntervalMs, store.Load().SampleIntervalMs);

        store.Save(new AppSettings { SampleIntervalMs = 250, CpuScale = CpuScale.Cpus, ActiveLayout = "Work" });
        var back = store.Load();
        Assert.Equal(250, back.SampleIntervalMs);
        Assert.Equal(CpuScale.Cpus, back.CpuScale);
        Assert.DoesNotContain("sampleInterval\"", File.ReadAllText(path));

        File.WriteAllText(path, "garbage");
        Assert.Equal(new AppSettings().SampleIntervalMs, store.Load().SampleIntervalMs);
    }
}
