using System.IO.Pipelines;
using System.IO.Pipes;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NockMon.Core.Agents;
using NockMon.Core.Etw;
using NockMon.Core.Metrics;
using NockMon.Core.Sampling;

namespace NockMon.Core.Tests;

public sealed class AgentTests : IDisposable
{
    private const int Steps = 30;
    private const int SpikeFrom = 11, SpikeTo = 15;

    private readonly FakeEvents _events = new();
    private readonly SamplingEngine _engine;
    private bool _shareCommandLines;

    /// <summary>
    /// 1 s samples, 2 s scans, 2 CPUs. The System Idle Process (PID 0) "burns" the idle time; a.exe 0.5 CPU throughout; b.exe 0.1 CPU, except 1.0 CPU over t = 11..15 s.
    /// After the run, sample 28 (t = 29 s) is the latest; history holds 29 samples.
    /// </summary>
    public AgentTests()
    {
        static double BCpu(int t) => Enumerable.Range(1, t).Sum(s => s is > SpikeFrom - 1 and <= SpikeTo ? 1.0 : 0.1);
        var probe = new FakeProbe(2, Enumerable.Range(0, Steps).Select(i => (Func<RawSample>)(() => Raw.Sample(i,
            [Raw.Process(0, "Idle", cpuSeconds: 1.25 * i), Raw.Process(10, "a.exe", cpuSeconds: 0.5 * i, workingSet: 100L << 20),
                Raw.Process(20, "b.exe", cpuSeconds: BCpu(i), workingSet: 50L << 20)],
            [Raw.Core(0.5 * i, 0.5 * i), Raw.Core(0.25 * i, 0.75 * i)]))).ToArray());
        _engine = new SamplingEngine(probe, new SamplingOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1)), _events);
        for (var i = 0; i < Steps; i++)
            _engine.SampleOnce();
    }

    public void Dispose() => _engine.Dispose();

    private MonitorQueries Queries => new(_engine, () => _shareCommandLines);

    private static double Minutes(double seconds) => seconds / 60;

    [Fact]
    public void TopProcesses_Now_RanksTheLatestScan()
    {
        var top = Queries.GetTopProcesses(ProcessField.Cpu, minutes: 0, endMinutesAgo: 0, count: 10, groupByName: false);
        Assert.Equal(["a.exe", "b.exe"], top.Rows.Select(r => r.Name));
        Assert.Equal(10, top.Rows[0].Pid);
        Assert.Equal(0.5, top.Rows[0].Value, 3);
        Assert.Equal(100, top.Rows[0].WorkingSetMB);
    }

    [Fact]
    public void TopProcesses_OverARange_FindsThePastSpike()
    {
        // The spike ended 14 s before the latest sample: over the last 10 s, b.exe is idle; over 15..9 s ago, it leads.
        var recent = Queries.GetTopProcesses(ProcessField.Cpu, Minutes(10), 0, 10, false);
        Assert.Equal("a.exe", recent.Rows[0].Name);

        var past = Queries.GetTopProcesses(ProcessField.Cpu, Minutes(6), Minutes(14), 10, false);
        Assert.Equal("b.exe", past.Rows[0].Name);
        Assert.Equal(1.0, past.Rows[0].Max!.Value, 3);
        Assert.Null(past.Rows[0].Pid); // Per image name over a range.

        var all = Queries.GetTopProcesses(ProcessField.Cpu, 60, 0, 10, false);
        Assert.DoesNotContain(all.Rows, r => r.Name == "Idle"); // Idle time is not load.
        var b = all.Rows.Single(r => r.Name == "b.exe");
        Assert.Equal(1.0, b.Max!.Value, 3);
        Assert.Equal("a.exe", all.Rows[0].Name); // Averages: 0.5 vs ~0.25.
    }

    [Fact]
    public void TopProcesses_RejectsFieldsWithoutHistoryOverARange()
    {
        var e = Assert.Throws<AgentException>(() => Queries.GetTopProcesses(ProcessField.VirtualSize, 5, 0, 10, false));
        Assert.Contains("no history", e.Message);
        Assert.NotEmpty(Queries.GetTopProcesses(ProcessField.VirtualSize, 0, 0, 10, false).Rows);
    }

    [Fact]
    public void MetricHistory_DownsamplesWithStats()
    {
        var history = Queries.GetMetricHistory("process.Cpu", "b", minutes: 60, endMinutesAgo: 0, points: 10);
        Assert.Equal("logical CPUs", history.Unit);
        Assert.Equal(10, history.Points.Count);
        Assert.Equal(1.0, history.Stats.Max, 3);
        Assert.Equal(0.1, history.Stats.Last, 3);
        Assert.All(history.Points, p => Assert.NotNull(p.Max)); // 29 samples in 10 points: buckets of 3.
        Assert.Contains("scan", history.Resolution);

        var cpu = Queries.GetMetricHistory("system.Cpu", null, 60, 0, 500);
        Assert.Equal(29, cpu.Points.Count);
        Assert.Null(cpu.Points[0].Max); // One sample per point: no min/max.
        Assert.Equal(0.75, cpu.Stats.Average, 3); // 0.5 + 0.25 CPU.
    }

    [Fact]
    public void MetricHistory_ValidatesIdsAndTargets()
    {
        Assert.Contains("list_metrics", Assert.Throws<AgentException>(() => Queries.GetMetricHistory("system.Nope", null, 5, 0, 10)).Message);
        Assert.Contains("0 to 1", Assert.Throws<AgentException>(() => Queries.GetMetricHistory("core.Usage", "7", 5, 0, 10)).Message);
        Assert.Contains("target", Assert.Throws<AgentException>(() => Queries.GetMetricHistory("process.Cpu", null, 5, 0, 10)).Message);
        Assert.Contains("no history", Assert.Throws<AgentException>(() => Queries.GetMetricHistory("process.VirtualSize", "a", 5, 0, 10)).Message);
        Assert.Contains("No GPU", Assert.Throws<AgentException>(() => Queries.GetMetricHistory("gpu.Temperature", null, 5, 0, 10)).Message);
        Assert.Equal(0.25, Queries.GetMetricHistory("CORE.usage", "1", 60, 0, 10).Stats.Average, 3);
    }

    [Fact]
    public void Process_CurrentInstancesAndHistory()
    {
        var b = Queries.GetProcess("b", 60);
        Assert.Equal(1, b.Instances);
        Assert.Equal(20, b.Current[0].Pid);
        var cpu = b.History.Single(h => h.Metric == "process.Cpu");
        Assert.Equal(1.0, cpu.Max, 3);
        Assert.Equal(0.1, cpu.Last, 3);
        Assert.Throws<AgentException>(() => Queries.GetProcess("nothing-like-this", 60));
    }

    [Fact]
    public void Overview_CurrentAndRecentWindows()
    {
        var o = Queries.GetOverview();
        Assert.Equal(2, o.LogicalProcessors);
        Assert.Equal(0.75, o.Current.Cpu, 3);
        Assert.Equal(1, o.SampleIntervalSeconds);
        Assert.Equal(2, o.ScanIntervalSeconds);
        Assert.Single(o.Recent); // 29 s of history: the 1 min window already covers it all.
        Assert.Equal("a.exe", o.TopCpu[0].Name);
    }

    [Fact]
    public void ProcessEvents_CommandLinesOnlyWhenShared()
    {
        _events.Recent.Add(new ProcessEvent(DateTime.UtcNow.AddSeconds(-5), ProcessEventKind.Start, 42, 10, "tool.exe", "tool.exe --token secret", null));
        _events.Recent.Add(new ProcessEvent(DateTime.UtcNow.AddMinutes(-30), ProcessEventKind.Exit, 41, 10, "old.exe", null, 0));

        var hidden = Queries.GetProcessEvents(10, null, 100);
        Assert.False(hidden.CommandLinesShared);
        Assert.Equal("tool.exe", Assert.Single(hidden.Events).Name); // old.exe is out of the range.
        Assert.Null(hidden.Events[0].CommandLine);

        _shareCommandLines = true;
        Assert.Equal("tool.exe --token secret", Queries.GetProcessEvents(10, "tool", 100).Events[0].CommandLine);
        Assert.Empty(Queries.GetProcessEvents(10, "other", 100).Events);
    }

    [Fact]
    public void MetricIds_AreUniqueAndDocumented()
    {
        var all = MetricIds.All().ToList();
        Assert.Equal(all.Count, all.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var entry in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Summary), entry.Id);
            Assert.False(string.IsNullOrWhiteSpace(MetricIds.Describe(entry.Id).Details), entry.Id);
        }
        Assert.DoesNotContain(all, e => e.Id == "gpu.Engine");
        Assert.Equal(new SystemSeries(SystemMetric.DpcMax), MetricIds.ToSeries("system.dpcmax", null, [], 4));
        Assert.Equal(new ProcessSeries(ProcessField.Cpu, "devenv"), MetricIds.ToSeries("process.Cpu", " devenv ", [], 4));
        Assert.Equal("NVIDIA GeForce RTX 4070", ((GpuSeries)MetricIds.ToSeries("gpu.Temperature", "4070", ["Intel(R) UHD", "NVIDIA GeForce RTX 4070"], 4)).Adapter);
        Assert.Throws<AgentException>(() => MetricIds.ToSeries("system.3", null, [], 4)); // Numeric enum values are not ids.
    }

    [Fact]
    public async Task Mcp_ToolsCallableOverTheProtocol()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var options = new MonitorTools(Queries).CreateServerOptions("1.2.3");
        await using var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), options);
        using var stop = new CancellationTokenSource();
        var run = server.RunAsync(stop.Token);

        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
                         cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal("nockmon", client.ServerInfo.Name);
            Assert.Contains("logical CPUs", client.ServerInstructions);

            var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(
                ["describe_metric", "get_kernel_activity", "get_metric_history", "get_overview", "get_process", "get_process_events", "get_top_processes", "list_metrics"],
                tools.Select(t => t.Name).Order());
            Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
            // Enum parameters reach the agent as their names.
            Assert.Contains("\"WorkingSet\"", tools.Single(t => t.Name == "get_top_processes").JsonSchema.GetRawText());

            var overview = await client.CallToolAsync("get_overview", cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEqual(true, overview.IsError);
            using (var json = JsonDocument.Parse(Text(overview)))
            {
                Assert.Equal(2, json.RootElement.GetProperty("logicalProcessors").GetInt32());
                Assert.Equal("a.exe", json.RootElement.GetProperty("topCpu")[0].GetProperty("name").GetString());
            }

            var top = await client.CallToolAsync("get_top_processes",
                new Dictionary<string, object?> { ["field"] = "Cpu", ["minutes"] = 0.1, ["endMinutesAgo"] = 14.0 / 60 },
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEqual(true, top.IsError);
            Assert.Contains("b.exe", Text(top));

            var bad = await client.CallToolAsync("get_metric_history", new Dictionary<string, object?> { ["metric"] = "cpu" },
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(bad.IsError);
            Assert.Contains("list_metrics", Text(bad)); // The agent learns how to fix its call.
        }

        stop.Cancel();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task PipeServer_ServesSessionsAndRefusesASecondOwner()
    {
        var name = "NockMon.Mcp.Test." + Guid.NewGuid().ToString("N");
        using var server = new AgentPipeServer(() => new MonitorTools(Queries).CreateServerOptions("1.0"), name);
        var changes = 0;
        server.SessionsChanged += () => Interlocked.Increment(ref changes);
        server.Start();

        await using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(5000, TestContext.Current.CancellationToken);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe),
                new McpClientOptions { ClientInfo = new Implementation { Name = "test-agent", Version = "1" } },
                cancellationToken: TestContext.Current.CancellationToken);
            var overview = await client.CallToolAsync("get_overview", cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEqual(true, overview.IsError);
            Assert.Equal(["test-agent"], server.Clients);

            // A second server on the same name (an impersonator, or a second instance) must not start.
            using var squatter = new AgentPipeServer(() => new MonitorTools(Queries).CreateServerOptions("1.0"), name);
            squatter.Start();
            await WaitUntil(() => squatter.Error is not null);
            Assert.Contains("Another process", squatter.Error);
        }

        await WaitUntil(() => server.Clients.Count == 0);
        Assert.True(changes >= 2);
        Assert.True(server.IsRunning);
    }

    [Fact]
    public void PipeSecurity_DeniesNetworkBeforeAllowingTheUser()
    {
        var sddl = AgentPipeServer.SecurityDescriptor("S-1-5-21-1-2-3-1001");
        Assert.StartsWith("D:P(D;;GA;;;NU)(A;;GA;;;S-1-5-21-1-2-3-1001)", sddl);
        Assert.EndsWith("S:(ML;;NW;;;ME)", sddl);
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition());
    }
}
