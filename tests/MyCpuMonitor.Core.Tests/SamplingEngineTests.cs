using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.Tests;

public class SamplingEngineTests
{
    private static FakeProbe Probe() => new(1,
        () => Raw.Sample(0, [Raw.Process(10, "a.exe", cpuSeconds: 0)], [Raw.Core(0, 0)]),
        () => Raw.Sample(1, [Raw.Process(10, "a.exe", cpuSeconds: 0.5)], [Raw.Core(0.5, 0.5)]),
        () => Raw.Sample(2, [Raw.Process(10, "a.exe", cpuSeconds: 0.75)], [Raw.Core(0.75, 1.25)]));

    [Fact]
    public void SampleOnce_PublishesSnapshotAndHistory()
    {
        using var engine = new SamplingEngine(Probe(), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        var sampled = 0;
        engine.Sampled += () => sampled++;

        Assert.False(engine.SampleOnce());
        Assert.True(engine.SampleOnce());
        Assert.True(engine.SampleOnce());
        Assert.Equal(2, sampled);

        var (index, cpu, history) = engine.Read(0, static (_, s, h) =>
        {
            var buffer = new float[10];
            var n = h.Read(new ProcessSeries(ProcessField.Cpu, "a"), buffer);
            return (s.SampleIndex, s.Processes[0].Cpu, buffer[..n]);
        });
        Assert.Equal(1, index);
        Assert.Equal(0.25, cpu, 6);
        Assert.Equal([0.5f, 0.25f], history);
        Assert.Equal(60, engine.Read(0, static (_, _, h) => h.Capacity));
    }

    [Fact]
    public void Configure_ResizesAndClearsHistory()
    {
        using var engine = new SamplingEngine(Probe(), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        engine.SampleOnce();
        engine.SampleOnce();
        engine.Configure(TimeSpan.FromMilliseconds(500), TimeSpan.FromMinutes(1));

        Assert.Equal((120, 0L), engine.Read(0, static (_, _, h) => (h.Capacity, h.TotalSamples)));
    }

    [Fact]
    public void InvalidConfiguration_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SamplingEngine(Probe(), TimeSpan.FromMilliseconds(10), TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SamplingEngine(Probe(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
    }
}
