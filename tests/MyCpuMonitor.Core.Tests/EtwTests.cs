using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Session;
using MyCpuMonitor.Core.Etw;
using MyCpuMonitor.Core.History;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.Tests;

internal sealed class FakeEvents : IKernelEventSource
{
    public KernelEventTotals Totals;
    public Dictionary<int, ProcessIoTotals> Processes = new();
    public List<DriverTotals> Drivers = [];
    public string? Status => null;
    public long EventsLost => 0;

    public void HarvestSample(out KernelEventTotals totals)
    {
        totals = Totals;
        Totals = default;
    }

    public void HarvestScan(Dictionary<int, ProcessIoTotals> processes, List<DriverTotals> drivers)
    {
        processes.Clear();
        drivers.Clear();
        foreach (var (k, v) in Processes)
            processes[k] = v;
        drivers.AddRange(Drivers);
        Processes.Clear();
        Drivers.Clear();
    }

    public void CopyRecentEvents(List<ProcessEvent> destination) => destination.Clear();

    public void Dispose()
    {
    }
}

public class EtwProcessingTests
{
    [Fact]
    public void SystemEventRates_OverSnapshotInterval()
    {
        var snapshot = new Snapshot { Interval = TimeSpan.FromSeconds(2) };
        SampleProcessor.ApplyEvents(snapshot, new KernelEventTotals
        {
            DiskReadBytes = 4000, DiskWriteBytes = 2000, DiskOps = 10, DiskLatencySumMs = 25,
            NetSendBytes = 100, NetRecvBytes = 300, DpcCount = 2000, DpcMaxMs = 1.5, IsrCount = 600, IsrMaxMs = 0.2,
        });
        var s = snapshot.System;
        Assert.Equal((2000, 1000, 5, 2.5), (s.DiskRead, s.DiskWrite, s.DiskOps, s.DiskLatencyMs));
        Assert.Equal((50, 150), (s.NetSend, s.NetRecv));
        Assert.Equal((1000, 300, 1.5, 0.2), (s.DpcRate, s.IsrRate, s.DpcMaxMs, s.IsrMaxMs));
    }

    [Fact]
    public void ScanEvents_PerProcessByPid_AndDrivers()
    {
        var table = new ProcessTable { Count = 2, Interval = TimeSpan.FromSeconds(2) };
        table.Items[0] = new ProcessStats { Pid = 10, Name = "a.exe", DiskOps = 99 };
        table.Items[1] = new ProcessStats { Pid = 20, Name = "b.exe", DiskOps = 99 }; // Stale values must be cleared.
        var io = new Dictionary<int, ProcessIoTotals> { [10] = new() { DiskReadBytes = 800, DiskOps = 4, DiskLatencySumMs = 8, NetRecvBytes = 200 } };
        List<DriverTotals> drivers = [new() { Name = "nvlddmkm.sys", DpcCount = 100, DpcTimeMs = 20, DpcMaxMs = 3 }];

        SampleProcessor.ApplyScanEvents(table, io, drivers);

        Assert.Equal((400, 2, 2, 100), (table.Items[0].DiskReadBytes, table.Items[0].DiskOps, table.Items[0].DiskLatencyMs, table.Items[0].NetRecvBytes));
        Assert.Equal(0, table.Items[1].DiskOps);
        var d = Assert.Single(table.Drivers);
        Assert.Equal(("nvlddmkm.sys", 10, 50, 3), (d.Name, d.DpcTime, d.DpcRate, d.DpcMaxMs));
    }

    [Fact]
    public void Grouping_WeightsDiskLatencyByOperations()
    {
        var s = new Snapshot { Table = new ProcessTable { Count = 2 } };
        s.Processes[0] = new ProcessStats { Pid = 1, Name = "x.exe", DiskOps = 1, DiskLatencyMs = 10 };
        s.Processes[1] = new ProcessStats { Pid = 2, Name = "x.exe", DiskOps = 3, DiskLatencyMs = 2 };
        var row = new ProcessQuery().Run(s, new ProcessQueryOptions(ProcessField.DiskOps, true, 1, GroupByName: true))[0];
        Assert.Equal(4, row.Stats.DiskOps);
        Assert.Equal(4, row.Stats.DiskLatencyMs, 9); // (10*1 + 2*3) / 4, not the mean of averages (6).
    }

    [Fact]
    public void DriverQuery_SortsAndTakesTop()
    {
        var table = new ProcessTable();
        table.Drivers.AddRange([
            new DriverStats { Name = "a.sys", DpcTime = 1, DpcMaxMs = 9 },
            new DriverStats { Name = "b.sys", DpcTime = 5, DpcMaxMs = 1 },
            new DriverStats { Name = "c.sys", DpcTime = 3, DpcMaxMs = 4 }]);
        var query = new DriverQuery();
        Assert.Equal(["b.sys", "c.sys"], query.Run(table, DriverField.DpcTime, true, 2).ToArray().Select(d => d.Name));
        Assert.Equal(["a.sys", "c.sys", "b.sys"], query.Run(table, DriverField.DpcMax, true, 5).ToArray().Select(d => d.Name));
    }

    [Fact]
    public void History_EtwFieldsStayAtScanResolution_EvenWhenWatched()
    {
        var h = new HistoryStore(8, 4, 1);
        var table = new ProcessTable { Count = 1 };
        table.Items[0] = new ProcessStats { Pid = 1, Name = "a.exe", DiskReadBytes = 100 };
        var watched = new WatchedAggregate("a"); // Handles know nothing about disk: zeros here must not win.
        h.AppendSample(new Snapshot { Cores = [new CoreStats()] }, [watched]);
        h.AppendScan(table);
        h.AppendSample(new Snapshot { Cores = [new CoreStats()] }, [watched]);

        var buffer = new float[2];
        h.Read(new ProcessSeries(ProcessField.DiskBytes, "a"), buffer);
        Assert.Equal([100f, 100f], buffer);
    }

    [Fact]
    public void Engine_HarvestsEventsAtSamplesAndScans()
    {
        var events = new FakeEvents();
        var probe = new FakeProbe(1,
            () => Raw.Sample(0, [Raw.Process(10, "a.exe")]),
            () => Raw.Sample(1, [Raw.Process(10, "a.exe")]),
            () => Raw.Sample(2, [Raw.Process(10, "a.exe")]));
        using var engine = new SamplingEngine(probe, new SamplingOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)), events);
        engine.SampleOnce();
        events.Totals = new KernelEventTotals { DpcCount = 500 };
        events.Processes[10] = new ProcessIoTotals { NetSendBytes = 1000 };
        events.Drivers.Add(new DriverTotals { Name = "tcpip.sys", DpcCount = 500 });
        engine.SampleOnce();

        var (dpc, net, drivers) = engine.Read(0, static (_, s, _) => (s.System.DpcRate, s.Processes[0].NetSendBytes, s.Table.Drivers.Count));
        Assert.Equal((500, 1000, 1), (dpc, net, drivers));
    }
}

/// <summary>Integration with a real kernel session (requires elevation; skipped otherwise).</summary>
public class EtwIntegrationTests
{
    [Fact]
    public void KernelSession_DeliversDiskDpcAndProcessEvents_AndStopsOnDispose()
    {
        if (!Environment.IsPrivilegedProcess)
            Assert.Skip("Kernel ETW sessions need elevation.");

        using (var monitor = new EtwKernelMonitor())
        {
            monitor.Start();
            Assert.Null(monitor.Status);
            Assert.NotNull(TraceEventSession.GetActiveSession(EtwKernelMonitor.SessionName));

            Thread.Sleep(1500); // Session startup.
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c echo mycpumon-etw-test") { UseShellExecute = false, CreateNoWindow = true })!)
                p.WaitForExit();
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "mycpumon-etw.bin"), new byte[1 << 20]);
            Thread.Sleep(2500); // Real-time buffers flush about every second.

            monitor.HarvestSample(out var totals);
            Assert.True(totals.DpcCount > 0, "No DPC events");
            var events = new List<ProcessEvent>();
            monitor.CopyRecentEvents(events);
            Assert.Contains(events, e => e.Kind == ProcessEventKind.Start && e.CommandLine?.Contains("mycpumon-etw-test") == true);
            Assert.Contains(events, e => e.Kind == ProcessEventKind.Exit && e.Name.Contains("cmd", StringComparison.OrdinalIgnoreCase));

            var io = new Dictionary<int, ProcessIoTotals>();
            var drivers = new List<DriverTotals>();
            monitor.HarvestScan(io, drivers);
            Assert.NotEmpty(drivers);
            Assert.DoesNotContain(drivers, d => d.Name == "unknown" && d.DpcCount > totals.DpcCount / 2);
        }
        Assert.Null(TraceEventSession.GetActiveSession(EtwKernelMonitor.SessionName));
    }

    [Fact]
    public void DriverMap_ResolvesKernelAddresses()
    {
        if (!Environment.IsPrivilegedProcess)
            Assert.Skip("Driver addresses need elevation.");
        var map = new DriverMap();
        map.Refresh();
        Assert.True(map.Count > 50);
        Assert.Equal("unknown", map.Resolve(1));
    }
}
