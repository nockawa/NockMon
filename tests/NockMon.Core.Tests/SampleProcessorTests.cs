using NockMon.Core.Sampling;

namespace NockMon.Core.Tests;

public class SampleProcessorTests
{
    private readonly SampleProcessor _processor = new();
    private readonly Snapshot _snapshot = new();
    private readonly ProcessTable _table = new();

    private ProcessTable Scan(params RawSample[] samples)
    {
        foreach (var s in samples)
            _processor.ProcessScan(s, _table);
        return _table;
    }

    private Snapshot System(params RawSample[] samples)
    {
        foreach (var s in samples)
            _processor.ProcessSystem(s, _snapshot);
        return _snapshot;
    }

    private static ProcessStats Find(ProcessTable t, int pid) => t.Span.ToArray().Single(p => p.Pid == pid);

    [Fact]
    public void FirstScan_IsBaselineOnly()
    {
        Assert.False(_processor.ProcessScan(Raw.Sample(0, [Raw.Process(10, "a.exe")]), _table));
        Assert.Equal(0, _table.Count);
    }

    [Fact]
    public void FirstSystemSample_IsBaselineOnly()
    {
        Assert.False(_processor.ProcessSystem(Raw.Sample(0, []), _snapshot));
        Assert.Empty(_snapshot.Cores);
    }

    [Fact]
    public void Cpu_IsCpuTimeDeltaOverWallTime_InCpus()
    {
        // Kernel 2 s -> 4 s, user 4 s -> 5 s over 2 s wall: 1.0 kernel + 0.5 user = 1.5 CPUs.
        var t = Scan(
            Raw.Sample(10, [Raw.Process(10, "a.exe", cpuSeconds: 6, kernelShare: 2.0 / 6)]),
            Raw.Sample(12, [Raw.Process(10, "a.exe", cpuSeconds: 9, kernelShare: 4.0 / 9)]));

        var p = Find(t, 10);
        Assert.Equal(1.5, p.Cpu, 6);
        Assert.Equal(1.0, p.CpuKernel, 6);
        Assert.Equal(0.5, p.CpuUser, 6);
        Assert.Equal(TimeSpan.FromSeconds(2), t.Interval);
    }

    [Fact]
    public void IoRates_ArePerSecond_AndSummed()
    {
        var t = Scan(
            Raw.Sample(0, [Raw.Process(10, "a.exe", readOps: 100, writeOps: 50)]),
            Raw.Sample(0.5, [Raw.Process(10, "a.exe", readOps: 300, writeOps: 60)]));

        var p = Find(t, 10);
        Assert.Equal(400, p.IoReadOps, 6);
        Assert.Equal(20, p.IoWriteOps, 6);
        Assert.Equal(420, p.IoOps, 6);
        Assert.Equal(420, t.IoOps, 6);
    }

    [Fact]
    public void ProcessCreatedDuringInterval_CountersStartAtZero()
    {
        var t = Scan(
            Raw.Sample(0, [Raw.Process(10, "a.exe")]),
            Raw.Sample(1, [Raw.Process(10, "a.exe"), Raw.Process(20, "new.exe", cpuSeconds: 0.25, readOps: 7)]));

        var p = Find(t, 20);
        Assert.Equal(0.25, p.Cpu, 6);
        Assert.Equal(7, p.IoReadOps, 6);
    }

    [Fact]
    public void PidReuse_WithDifferentCreateTime_IsANewProcess()
    {
        var t = Scan(
            Raw.Sample(0, [Raw.Process(10, "old.exe", cpuSeconds: 100, createTime: 1)]),
            Raw.Sample(1, [Raw.Process(10, "new.exe", cpuSeconds: 0.5, createTime: 2)]));

        // Delta against the old process would be negative (clamped to 0); new identity gives the true 0.5.
        Assert.Equal(0.5, Find(t, 10).Cpu, 6);
        Assert.Equal("new.exe", Find(t, 10).Name);
    }

    [Fact]
    public void ExitedProcesses_AreDropped()
    {
        var t = Scan(
            Raw.Sample(0, [Raw.Process(10, "a.exe"), Raw.Process(11, "b.exe")]),
            Raw.Sample(1, [Raw.Process(10, "a.exe")]));

        Assert.Equal(1, t.Count);
        Assert.Equal(10, t.Items[0].Pid);
    }

    [Fact]
    public void Cores_UsageFromIdleDelta_SystemCpuIsSum()
    {
        RawCore[] t0 = [Raw.Core(0, 0), Raw.Core(0, 0)];
        // CPU 0: 0.75 s busy (0.25 kernel, 0.1 DPC) + 0.25 idle. CPU 1: fully idle.
        RawCore[] t1 = [Raw.Core(0.75, 0.25, kernelBusySeconds: 0.25, dpcSeconds: 0.1), Raw.Core(0, 1)];
        var s = System(Raw.Sample(0, [], t0), Raw.Sample(1, [], t1));

        Assert.Equal(0.75, s.Cores[0].Usage, 6);
        Assert.Equal(0.25, s.Cores[0].Kernel, 6);
        Assert.Equal(0.5, s.Cores[0].User, 6);
        Assert.Equal(0.1, s.Cores[0].Dpc, 6);
        Assert.Equal(0, s.Cores[1].Usage, 6);
        Assert.Equal(0.75, s.System.Cpu, 6);
        Assert.Equal(2, s.LogicalProcessors);
    }

    [Fact]
    public void SystemAndScan_HaveIndependentBaselines()
    {
        Assert.False(_processor.ProcessSystem(Raw.Sample(0, []), _snapshot));
        Assert.True(_processor.ProcessSystem(Raw.Sample(1, []), _snapshot));
        Assert.False(_processor.ProcessScan(Raw.Sample(1, []), _table)); // First scan at t=1.
        Assert.True(_processor.ProcessScan(Raw.Sample(3, []), _table));
    }

    [Fact]
    public void NonIncreasingTime_Throws()
    {
        _processor.ProcessScan(Raw.Sample(1, []), _table);
        Assert.Throws<ArgumentException>(() => _processor.ProcessScan(Raw.Sample(1, []), _table));
        _processor.ProcessSystem(Raw.Sample(1, []), _snapshot);
        Assert.Throws<ArgumentException>(() => _processor.ProcessSystem(Raw.Sample(1, []), _snapshot));
    }

    [Fact]
    public void Reset_RestartsBaselines()
    {
        Scan(Raw.Sample(0, []), Raw.Sample(1, []));
        System(Raw.Sample(0, []), Raw.Sample(1, []));
        _processor.Reset();
        Assert.False(_processor.ProcessScan(Raw.Sample(2, []), _table));
        Assert.False(_processor.ProcessSystem(Raw.Sample(2, []), _snapshot));
    }
}
