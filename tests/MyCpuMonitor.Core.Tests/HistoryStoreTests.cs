using MyCpuMonitor.Core.History;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.Tests;

public class HistoryStoreTests
{
    private static Snapshot Snap(double systemCpu, params (string Name, double Cpu)[] processes)
    {
        var s = new Snapshot { Cores = [new CoreStats { Usage = systemCpu }], ProcessCount = processes.Length };
        s.System.Cpu = systemCpu;
        for (var i = 0; i < processes.Length; i++)
            s.Processes[i] = new ProcessStats { Pid = i + 1, Name = processes[i].Name, Cpu = processes[i].Cpu };
        return s;
    }

    private static float[] Read(HistoryStore h, SeriesRef series, int count)
    {
        var buffer = new float[count];
        var n = h.Read(series, buffer);
        return buffer[..n];
    }

    [Fact]
    public void Read_ReturnsLatestOldestFirst_AcrossWrap()
    {
        var h = new HistoryStore(capacity: 4, coreCount: 1);
        for (var i = 1; i <= 6; i++)
            h.Append(Snap(i));

        Assert.Equal(6, h.TotalSamples);
        Assert.Equal(4, h.Count);
        Assert.Equal([3f, 4f, 5f, 6f], Read(h, new SystemSeries(SystemMetric.Cpu), 10));
        Assert.Equal([5f, 6f], Read(h, new SystemSeries(SystemMetric.Cpu), 2));
        Assert.Equal([5f, 6f], Read(h, new CoreSeries(CoreMetric.Usage, 0), 2));
    }

    [Fact]
    public void ProcessSeries_SumsInstancesAndMatchingNames()
    {
        var h = new HistoryStore(4, 1);
        h.Append(Snap(0, ("chrome.exe", 0.5), ("chrome.exe", 0.25), ("code.exe", 1)));
        h.Append(Snap(0, ("chrome.exe", 1)));

        Assert.Equal([0.75f, 1f], Read(h, new ProcessSeries(ProcessField.Cpu, "chrome"), 2));
        Assert.Equal([2f, 1f], Read(h, new ProcessSeries(ProcessField.Instances, "chrome.exe"), 2));
        Assert.Equal([1.75f, 1f], Read(h, new ProcessSeries(ProcessField.Cpu, "chrome*;code*"), 2));
    }

    [Fact]
    public void ProcessSeries_IsZeroWhenAbsent_AndBeforeFirstSeen()
    {
        var h = new HistoryStore(4, 1);
        h.Append(Snap(0));
        h.Append(Snap(0, ("a.exe", 1)));
        h.Append(Snap(0));

        Assert.Equal([0f, 1f, 0f], Read(h, new ProcessSeries(ProcessField.Cpu, "a.exe"), 3));
    }

    [Fact]
    public void NameSeries_EvictedAfterAFullWindowAbsent()
    {
        var h = new HistoryStore(3, 1);
        h.Append(Snap(0, ("a.exe", 1)));
        for (var i = 0; i < 2; i++)
            h.Append(Snap(0));
        Assert.Contains("a.exe", h.ProcessNames); // Still in the 3-sample window.

        h.Append(Snap(0));
        Assert.DoesNotContain("a.exe", h.ProcessNames);
    }

    [Fact]
    public void ProcessField_WithoutHistory_Throws()
    {
        var h = new HistoryStore(4, 1);
        Assert.Throws<ArgumentException>(() => h.Read(new ProcessSeries(ProcessField.Pid, "*"), new float[4]));
    }

    [Fact]
    public void Timestamps_AlignWithValues()
    {
        var h = new HistoryStore(2, 1);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++)
        {
            var s = Snap(i);
            s.WallTimeUtc = t0.AddSeconds(i);
            h.Append(s);
        }
        var times = new DateTime[2];
        Assert.Equal(2, h.ReadTimestamps(times));
        Assert.Equal([t0.AddSeconds(1), t0.AddSeconds(2)], times);
    }

    [Fact]
    public void CoreCountMismatch_Throws()
    {
        var h = new HistoryStore(4, 2);
        Assert.Throws<ArgumentException>(() => h.Append(Snap(0)));
    }
}
