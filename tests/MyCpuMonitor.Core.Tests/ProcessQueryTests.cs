using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Persistence;
using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.Tests;

public class ProcessQueryTests
{
    private static Snapshot Snap(params (int Pid, string Name, double Cpu, long Private)[] processes)
    {
        var s = new Snapshot { ProcessCount = processes.Length };
        for (var i = 0; i < processes.Length; i++)
        {
            var (pid, name, cpu, priv) = processes[i];
            s.Processes[i] = new ProcessStats { Pid = pid, Name = name, Cpu = cpu, PrivateBytes = priv, CreateTime = 100 + pid, Handles = 10 };
        }
        return s;
    }

    private static readonly Snapshot Sample = Snap(
        (1, "chrome.exe", 0.5, 300), (2, "code.exe", 2.0, 100), (3, "chrome.exe", 1.0, 200), (4, "idle.exe", 0, 50));

    [Fact]
    public void SortsDescending_AndTakesTop()
    {
        var rows = new ProcessQuery().Run(Sample, new ProcessQueryOptions(ProcessField.Cpu, true, 2));
        Assert.Equal([2, 3], rows.ToArray().Select(r => r.Stats.Pid));
    }

    [Fact]
    public void SortsAscending_ByName_TiesByPid()
    {
        var rows = new ProcessQuery().Run(Sample, new ProcessQueryOptions(ProcessField.Name, false, 10));
        Assert.Equal([1, 3, 2, 4], rows.ToArray().Select(r => r.Stats.Pid));
    }

    [Fact]
    public void GroupByName_SumsAndCountsInstances()
    {
        var rows = new ProcessQuery().Run(Sample, new ProcessQueryOptions(ProcessField.PrivateBytes, true, 10, GroupByName: true)).ToArray();

        Assert.Equal(["chrome.exe", "code.exe", "idle.exe"], rows.Select(r => r.Stats.Name));
        var chrome = rows[0];
        Assert.Equal(2, chrome.Instances);
        Assert.Equal(1.5, chrome.Stats.Cpu, 9);
        Assert.Equal(500, chrome.Stats.PrivateBytes);
        Assert.Equal(20, chrome.Stats.Handles);
        Assert.Equal(101, chrome.Stats.CreateTime);
        Assert.Equal(2, chrome.GetValue(ProcessField.Instances));
    }

    [Fact]
    public void Filter_UsesNamePattern()
    {
        var rows = new ProcessQuery().Run(Sample, new ProcessQueryOptions(ProcessField.Cpu, true, 10, Filter: "chrome;idle"));
        Assert.Equal([3, 1, 4], rows.ToArray().Select(r => r.Stats.Pid));
    }

    [Fact]
    public void SteadyState_DoesNotAllocate()
    {
        var query = new ProcessQuery();
        var options = new ProcessQueryOptions(ProcessField.Cpu, true, 3, GroupByName: true, Filter: "*");
        query.Run(Sample, options);

        var before = GC.GetAllocatedBytesForCurrentThread();
        query.Run(Sample, options);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void WidgetClone_IsDeepAndPreservesType()
    {
        var original = new ChartWidgetConfig
        {
            Title = "x",
            Bounds = new GridRect(1, 2, 3, 4),
            Series = [new ChartSeriesConfig { Source = new ProcessSeries(ProcessField.Cpu, "devenv"), Color = "#112233" }],
        };
        var clone = LayoutStore.Clone(original);

        Assert.NotSame(original, clone);
        Assert.NotSame(original.Series, clone.Series);
        Assert.Equal(original.Id, clone.Id);
        Assert.Equal(original.Series[0].Source, clone.Series[0].Source);
        clone.Series.Clear();
        Assert.Single(original.Series);
    }

    [Theory]
    [InlineData(0.0, MetricUnit.Count, 1.0)]
    [InlineData(-5.0, MetricUnit.Count, 1.0)]
    [InlineData(0.7, MetricUnit.Cpu, 1.0)]
    [InlineData(1.0, MetricUnit.Cpu, 1.0)]
    [InlineData(1.2, MetricUnit.Cpu, 2.0)]
    [InlineData(3.0, MetricUnit.PerSecond, 5.0)]
    [InlineData(5100.0, MetricUnit.PerSecond, 10000.0)]
    [InlineData(1.86 * 1024 * 1024 * 1024, MetricUnit.Bytes, 2.0 * 1024 * 1024 * 1024)]
    [InlineData(300.0 * 1024 * 1024, MetricUnit.Bytes, 500.0 * 1024 * 1024)]
    [InlineData(999.0 * 1024, MetricUnit.BytesPerSecond, 1000.0 * 1024)]
    public void NiceCeiling(double value, MetricUnit unit, double expected) =>
        Assert.Equal(expected, AxisScale.NiceCeiling(value, unit), 6);

    [Fact]
    public void Results_ExposeLastRun()
    {
        var query = new ProcessQuery();
        query.Run(Sample, new ProcessQueryOptions(ProcessField.Cpu, true, 1));
        Assert.Equal(2, Assert.Single(query.Results.ToArray()).Stats.Pid);
    }

    [Fact]
    public void DefaultLabels()
    {
        Assert.Equal("CPU", new SystemSeries(SystemMetric.Cpu).DefaultLabel);
        Assert.Equal("Core 3 DPC", new CoreSeries(CoreMetric.Dpc, 3).DefaultLabel);
        Assert.Equal("devenv WS", new ProcessSeries(ProcessField.WorkingSet, "devenv").DefaultLabel);
    }
}
