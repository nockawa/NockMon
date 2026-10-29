using NockMon.Core.Etw;
using NockMon.Core.Gpu;
using NockMon.Core.History;
using NockMon.Core.Metrics;
using NockMon.Core.Model;
using NockMon.Core.Sampling;

namespace NockMon.Core.Agents;

/// <summary>
/// Read-only answers for AI agents, computed from the engine's snapshot and history. Each query holds the engine lock
/// only to copy what it needs (series are a few thousand floats), and returns small records in agent units
/// (<see cref="AgentUnits"/>).
/// </summary>
/// <param name="shareCommandLines">Read at each query: whether process command lines may be returned (user setting).</param>
public sealed class MonitorQueries(SamplingEngine engine, Func<bool> shareCommandLines)
{
    private const int MaxPoints = 500;
    private const int MaxRows = 100;
    private static readonly double[] OverviewWindows = [1, 5, 15];

    public Overview GetOverview()
    {
        var interval = engine.Interval;
        var scanSeconds = interval.TotalSeconds * engine.Options.ScanEvery; // Options locks: read before Read.
        var maxSamples = Samples(OverviewWindows[^1], interval);
        return engine.Read(maxSamples, (n, snap, history) =>
        {
            var s = snap.System;
            var current = new SystemNow(
                Cpu: R(s.Cpu), CpuKernel: R(s.CpuKernel), CpuDpc: R(s.CpuDpc), CpuInterrupt: R(s.CpuInterrupt),
                MemoryUsedMB: AgentUnits.Mb(s.PhysicalUsed), MemoryTotalMB: AgentUnits.Mb(s.PhysicalTotal),
                CommitMB: AgentUnits.Mb(s.CommitTotal), CommitLimitMB: AgentUnits.Mb(s.CommitLimit),
                StandbyMB: AgentUnits.Mb(s.MemoryStandby), FreeMB: AgentUnits.Mb(s.MemoryFree + s.MemoryZeroed),
                DiskReadMBps: AgentUnits.Mb(s.DiskRead), DiskWriteMBps: AgentUnits.Mb(s.DiskWrite), DiskLatencyMs: R(s.DiskLatencyMs),
                NetSendMBps: AgentUnits.Mb(s.NetSend), NetRecvMBps: AgentUnits.Mb(s.NetRecv),
                Processes: s.Processes, Threads: s.Threads, Handles: s.Handles,
                ContextSwitchesPerSec: R(s.ContextSwitches), ProcessorQueue: R(s.ProcessorQueueLength),
                LongestDpcMs: R(s.DpcMaxMs), LongestIsrMs: R(s.IsrMaxMs), TimerResolutionMs: R(s.TimerResolutionMs), CpuFrequencyMHz: R(s.FrequencyMhz));

            var count = (int)Math.Min(n, history.Count);
            var cpu = ReadSeries(history, new SystemSeries(SystemMetric.Cpu), count);
            var memory = ReadSeries(history, new SystemSeries(SystemMetric.PhysicalUsed), count);
            var disk = Sum(ReadSeries(history, new SystemSeries(SystemMetric.DiskRead), count), ReadSeries(history, new SystemSeries(SystemMetric.DiskWrite), count));
            var net = Sum(ReadSeries(history, new SystemSeries(SystemMetric.NetSend), count), ReadSeries(history, new SystemSeries(SystemMetric.NetRecv), count));
            var dpc = ReadSeries(history, new SystemSeries(SystemMetric.DpcMax), count);
            var times = new DateTime[count];
            history.ReadTimestamps(times);

            var recent = new List<WindowStats>();
            foreach (var minutes in OverviewWindows)
            {
                var k = Math.Min(count, Samples(minutes, interval));
                if (k == 0)
                    break;
                var from = count - k;
                var cpuMax = MaxAt(cpu, from);
                var dpcMax = MaxAt(dpc, from);
                recent.Add(new WindowStats(minutes, R(Average(cpu, from)), R(cpu[cpuMax]), AgentUnits.Time(times[cpuMax]),
                    AgentUnits.Mb(memory[MaxAt(memory, from)]), AgentUnits.Mb(Average(disk, from)), AgentUnits.Mb(disk[MaxAt(disk, from)]),
                    AgentUnits.Mb(Average(net, from)), AgentUnits.Mb(net[MaxAt(net, from)]), R(dpc[dpcMax]), AgentUnits.Time(times[dpcMax])));
                if (k == count)
                    break; // Less history than the next window: it would repeat this one.
            }

            var gpus = snap.Gpus.Where(g => g[GpuMetric.DedicatedTotal] > 0 || double.IsFinite(g[GpuMetric.Utilization])).Select(g => new GpuNow(g.Name, Optional(g[GpuMetric.Utilization]),
                OptionalMb(g[GpuMetric.DedicatedUsed]), OptionalMb(g[GpuMetric.DedicatedTotal]),
                Optional(g[GpuMetric.Temperature]), Optional(g[GpuMetric.Power]))).ToList();

            return new Overview(
                Now: AgentUnits.DateTime(snap.WallTimeUtc == default ? DateTime.UtcNow : snap.WallTimeUtc),
                Machine: Environment.MachineName, Os: Environment.OSVersion.VersionString, LogicalProcessors: snap.LogicalProcessors,
                SampleIntervalSeconds: interval.TotalSeconds, ScanIntervalSeconds: scanSeconds,
                HistoryMinutes: R(history.Count * interval.TotalMinutes), HistoryRetentionMinutes: R(history.Capacity * interval.TotalMinutes),
                KernelEvents: engine.KernelEvents is { } events ? events.Status ?? "running" : "unavailable",
                Current: current, Recent: recent, Gpus: gpus,
                TopCpu: TopNow(snap.Table.Span, ProcessField.Cpu, 5), TopMemory: TopNow(snap.Table.Span, ProcessField.PrivateBytes, 5));
        });
    }

    public MetricHistory GetMetricHistory(string metric, string? target, double minutes, double endMinutesAgo, int points)
    {
        var series = MetricIds.ToSeries(metric, target, engine.GpuAdapters.Select(a => a.Name).ToList(), engine.LogicalProcessorCount);
        var interval = engine.Interval;
        points = Math.Clamp(points, 1, MaxPoints);
        var (values, times, scanned) = engine.Read((series, minutes, endMinutesAgo), (state, _, history) =>
        {
            var (first, end) = ToWindow(history, interval, state.minutes, state.endMinutesAgo);
            var n = (int)(end - first);
            var tail = (int)(history.TotalSamples - first); // Read returns the latest samples: read up to the window end.
            var all = ReadSeries(history, state.series, tail);
            var stamps = new DateTime[tail];
            history.ReadTimestamps(stamps);
            var watched = state.series is ProcessSeries p && history.IsWatched(p.Pattern) && MetricCatalog.IsWatchedField(p.Field);
            return (all[..n], stamps[..n], state.series is ProcessSeries or ObjectTypeSeries && !watched);
        });
        if (values.Length == 0)
            throw new AgentException(minutes <= 0 ? "minutes must be positive." : "No history in that time range yet.");

        var unit = series.Info.Unit;
        var bucket = (int)Math.Ceiling(values.Length / (double)points);
        var result = new List<HistoryPoint>();
        for (var start = 0; start < values.Length; start += bucket)
        {
            var span = values.AsSpan(start, Math.Min(bucket, values.Length - start));
            float min = float.MaxValue, max = float.MinValue;
            double sum = 0;
            foreach (var v in span)
            {
                min = Math.Min(min, v);
                max = Math.Max(max, v);
                sum += v;
            }
            result.Add(bucket == 1
                ? new HistoryPoint(AgentUnits.Time(times[start]), AgentUnits.Convert(unit, sum), null, null)
                : new HistoryPoint(AgentUnits.Time(times[start]), AgentUnits.Convert(unit, sum / span.Length), AgentUnits.Convert(unit, min), AgentUnits.Convert(unit, max)));
        }

        var sorted = values.ToArray();
        Array.Sort(sorted);
        var maxAt = MaxAt(values, 0);
        var stats = new SeriesStats(AgentUnits.Convert(unit, sorted[0]), AgentUnits.Convert(unit, Average(values, 0)),
            AgentUnits.Convert(unit, values[maxAt]), AgentUnits.Time(times[maxAt]),
            AgentUnits.Convert(unit, sorted[(int)Math.Min(sorted.Length - 1, Math.Ceiling(sorted.Length * 0.95) - 1)]),
            AgentUnits.Convert(unit, values[^1]));
        var resolution = scanned
            ? $"process scan ({interval.TotalSeconds * engine.Options.ScanEvery:0.#} s): values hold between scans"
            : $"sample ({interval.TotalSeconds:0.###} s)";
        return new MetricHistory(metric, series.DefaultLabel, AgentUnits.Label(unit), AgentUnits.Time(times[0]), AgentUnits.Time(times[^1]),
            R(bucket * interval.TotalSeconds), resolution, stats, result);
    }

    public TopProcesses GetTopProcesses(ProcessField field, double minutes, double endMinutesAgo, int count, bool groupByName)
    {
        if (field == ProcessField.Name)
            throw new AgentException("Sort by a numeric field (Cpu, WorkingSet, PrivateBytes, DiskBytes…).");
        count = Math.Clamp(count, 1, MaxRows);
        var info = MetricCatalog.Get(field);
        var unit = AgentUnits.Label(info.Unit);
        if (minutes <= 0)
        {
            return engine.Read((field, count, groupByName), (state, snap, _) =>
            {
                var at = AgentUnits.Time(snap.Table.WallTimeUtc);
                var rows = state.groupByName ? TopGroups(snap.Table.Span, state.field, state.count) : TopNow(snap.Table.Span, state.field, state.count)
                    .Select(p => new TopRow(p.Name, p.Pid, null, p.Value, null, null, p.Cpu, p.WorkingSetMB)).ToList();
                return new TopProcesses(state.field.ToString(), unit, at, at, "latest process scan", rows);
            });
        }

        if (!info.HasHistory)
            throw new AgentException($"{field} has no history: use minutes = 0 (current values), or one of " +
                $"{string.Join(", ", MetricCatalog.ProcessHistoryFields)} for a time range.");
        var interval = engine.Interval;
        return engine.Read((field, minutes, endMinutesAgo, count), (state, snap, history) =>
        {
            var (first, end) = ToWindow(history, interval, state.minutes, state.endMinutesAgo);
            var aggregates = new List<NameAggregate>();
            history.AggregateNames(state.field, first, end, aggregates);
            var idle = IdleName(snap.Table.Span);
            var rows = aggregates.Where(a => !a.Name.Equals(idle, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Average).Take(state.count)
                .Select(a => new TopRow(a.Name, null, null, AgentUnits.Convert(info.Unit, a.Average), AgentUnits.Convert(info.Unit, a.Max),
                    history.TimeOf(a.MaxSample) is { } t ? AgentUnits.Time(t) : null, null, null))
                .ToList();
            return new TopProcesses(state.field.ToString(), unit, TimeAt(history, first), TimeAt(history, Math.Max(first, end - 1)),
                "per image name (instances summed), process scans; value = average over the range", rows);
        });
    }

    public ProcessDetails GetProcess(string pattern, double minutes)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new AgentException($"name is required: {MetricIds.ProcessTarget}.");
        pattern = pattern.Trim();
        var interval = engine.Interval;
        return engine.Read((pattern, minutes), (state, snap, history) =>
        {
            var match = new NamePattern(state.pattern);
            var instances = new List<ProcessInstance>();
            var total = 0;
            foreach (ref readonly var p in snap.Table.Span)
            {
                if (p.Pid == 0 || !match.IsMatch(p.Name))
                    continue;
                if (++total <= 50)
                    instances.Add(Instance(in p));
            }
            instances.Sort((a, b) => b.Cpu.CompareTo(a.Cpu));

            var (first, end) = ToWindow(history, interval, Math.Max(state.minutes, interval.TotalMinutes), 0);
            var n = (int)(end - first);
            var times = new DateTime[n];
            history.ReadTimestamps(times);
            var summaries = new List<FieldSummary>();
            foreach (var field in MetricCatalog.ProcessHistoryFields)
            {
                var values = ReadSeries(history, new ProcessSeries(field, state.pattern), n);
                if (n == 0 || values.AsSpan().IndexOfAnyExcept(0f) < 0)
                    continue;
                var unit = MetricCatalog.Get(field).Unit;
                var maxAt = MaxAt(values, 0);
                summaries.Add(new FieldSummary($"process.{field}", AgentUnits.Label(unit), AgentUnits.Convert(unit, Average(values, 0)),
                    AgentUnits.Convert(unit, values[maxAt]), AgentUnits.Time(times[maxAt]), AgentUnits.Convert(unit, values[^1])));
            }
            if (total == 0 && summaries.Count == 0)
                throw new AgentException($"No process matches '{state.pattern}' now or in the last {state.minutes:0.#} minutes.");
            return new ProcessDetails(state.pattern, total, n > 0 ? AgentUnits.Time(times[0]) : null, n > 0 ? AgentUnits.Time(times[^1]) : null,
                instances, summaries);
        });
    }

    public KernelActivity GetKernelActivity(double minutes, int count)
    {
        count = Math.Clamp(count, 1, MaxRows);
        var interval = engine.Interval;
        return engine.Read((minutes, count), (state, snap, history) =>
        {
            var s = snap.System;
            var (first, end) = ToWindow(history, interval, Math.Max(state.minutes, interval.TotalMinutes), 0);
            var n = (int)(end - first);
            var times = new DateTime[n];
            history.ReadTimestamps(times);
            var dpc = ReadSeries(history, new SystemSeries(SystemMetric.DpcMax), n);
            var isr = ReadSeries(history, new SystemSeries(SystemMetric.IsrMax), n);
            var dpcAt = n > 0 ? MaxAt(dpc, 0) : -1;
            var isrAt = n > 0 ? MaxAt(isr, 0) : -1;
            var now = new KernelNow(R(s.CpuDpc), R(s.CpuInterrupt), R(s.DpcRate), R(s.IsrRate), R(s.DpcMaxMs), R(s.IsrMaxMs),
                dpcAt >= 0 ? R(dpc[dpcAt]) : 0, dpcAt >= 0 ? AgentUnits.Time(times[dpcAt]) : null,
                isrAt >= 0 ? R(isr[isrAt]) : 0, isrAt >= 0 ? AgentUnits.Time(times[isrAt]) : null,
                AgentUnits.Mb(s.KernelNonPaged), AgentUnits.Mb(s.KernelPaged), R(s.Interrupts), R(s.ContextSwitches), R(s.SystemCalls));

            var table = snap.Table;
            var drivers = table.Drivers.OrderByDescending(d => d.DpcTime + d.IsrTime).Take(state.count)
                .Select(d => new DriverRow(d.Name, R(d.DpcTime), R(d.DpcRate), R(d.DpcMaxMs), R(d.IsrTime), R(d.IsrRate), R(d.IsrMaxMs))).ToList();
            var tags = table.PoolTagSpan.ToArray();
            PoolTagRow Tag(in PoolTagStats t) => new(t.Tag, AgentUnits.Mb(t.NonPagedBytes), AgentUnits.Mb(t.PagedBytes),
                R((t.NonPagedGrowth + t.PagedGrowth) / 1024), R(t.AllocationRate), t.Outstanding);
            var biggest = tags.OrderByDescending(t => t.NonPagedBytes + t.PagedBytes).Take(state.count).Select(t => Tag(in t)).ToList();
            var growing = tags.Where(t => t.NonPagedGrowth + t.PagedGrowth > 0).OrderByDescending(t => t.NonPagedGrowth + t.PagedGrowth)
                .Take(state.count).Select(t => Tag(in t)).ToList();
            var objects = table.ObjectTypeSpan.ToArray().Where(o => o.ObjectGrowth > 0 || o.HandleGrowth > 0)
                .OrderByDescending(o => Math.Max(o.ObjectGrowth, o.HandleGrowth)).Take(state.count)
                .Select(o => new ObjectTypeRow(o.Name, o.Objects, o.Handles, R(o.ObjectGrowth), R(o.HandleGrowth))).ToList();

            return new KernelActivity(engine.KernelEvents is { } events ? events.Status ?? "running" : "unavailable",
                n > 0 ? AgentUnits.Time(times[0]) : null, AgentUnits.Time(table.WallTimeUtc), now, drivers, biggest, growing, objects);
        });
    }

    public ProcessEvents GetProcessEvents(double minutes, string? name, int count)
    {
        count = Math.Clamp(count, 1, MaxRows * 5);
        var share = shareCommandLines();
        if (engine.KernelEvents is not { } source)
            return new ProcessEvents("unavailable", share, []);
        var events = new List<ProcessEvent>();
        source.CopyRecentEvents(events);
        var since = DateTime.UtcNow - TimeSpan.FromMinutes(minutes <= 0 ? 10 : minutes);
        var match = string.IsNullOrWhiteSpace(name) ? null : new NamePattern(name.Trim());
        var rows = events.Where(e => e.TimeUtc >= since && (match is null || match.IsMatch(e.Name)))
            .TakeLast(count)
            .Select(e => new ProcessEventRow(AgentUnits.Time(e.TimeUtc), e.Kind.ToString(), e.Pid, e.ParentPid, e.Name, e.ExitCode,
                share ? e.CommandLine : null))
            .ToList();
        return new ProcessEvents(source.Status ?? "running", share, rows);
    }

    // --- Helpers ----------------------------------------------------------------------------------------------------

    private static double R(double value) => AgentUnits.Round(value);

    private static double? Optional(double value) => double.IsFinite(value) ? R(value) : null;

    private static double? OptionalMb(double bytes) => double.IsFinite(bytes) ? AgentUnits.Mb(bytes) : null;

    private static int Samples(double minutes, TimeSpan interval) => (int)Math.Ceiling(Math.Max(0, minutes) * 60 / interval.TotalSeconds);

    /// <summary>Sample range [first, end) of <paramref name="minutes"/> ending <paramref name="endMinutesAgo"/> ago, clamped to the history.</summary>
    private static (long First, long End) ToWindow(HistoryStore history, TimeSpan interval, double minutes, double endMinutesAgo)
    {
        var oldest = history.TotalSamples - history.Count;
        var end = Math.Max(oldest, history.TotalSamples - Samples(endMinutesAgo, interval));
        var first = Math.Max(oldest, end - Samples(minutes, interval));
        return (first, end);
    }

    private static string? TimeAt(HistoryStore history, long sample) => history.TimeOf(sample) is { } t ? AgentUnits.Time(t) : null;

    private static float[] ReadSeries(HistoryStore history, SeriesRef series, int count)
    {
        var values = new float[Math.Min(count, history.Count)];
        history.Read(series, values);
        return values;
    }

    private static float[] Sum(float[] a, float[] b)
    {
        for (var i = 0; i < a.Length; i++)
            a[i] += b[i];
        return a;
    }

    private static double Average(float[] values, int from)
    {
        double sum = 0;
        for (var i = from; i < values.Length; i++)
            sum += values[i];
        return values.Length > from ? sum / (values.Length - from) : 0;
    }

    /// <summary>Index of the maximum from <paramref name="from"/> (the latest one on ties: closest to now).</summary>
    private static int MaxAt(float[] values, int from)
    {
        var best = from;
        for (var i = from; i < values.Length; i++)
        {
            if (values[i] >= values[best])
                best = i;
        }
        return best;
    }

    /// <summary>Name of the System Idle Process (PID 0, "Idle"): its "CPU" is idle time, not load.</summary>
    private static string IdleName(ReadOnlySpan<ProcessStats> processes)
    {
        foreach (ref readonly var p in processes)
        {
            if (p.Pid == 0)
                return p.Name;
        }
        return "Idle";
    }

    private static List<ProcessNow> TopNow(ReadOnlySpan<ProcessStats> processes, ProcessField field, int count)
    {
        var unit = MetricCatalog.Get(field).Unit;
        var rows = new List<(double Value, ProcessNow Row)>();
        foreach (ref readonly var p in processes)
        {
            if (p.Pid == 0)
                continue; // System Idle Process.
            var v = p.GetValue(field);
            rows.Add((v, new ProcessNow(p.Name, p.Pid, AgentUnits.Convert(unit, v), R(p.Cpu), AgentUnits.Mb(p.WorkingSet))));
        }
        return rows.OrderByDescending(r => r.Value).Take(count).Select(r => r.Row).ToList();
    }

    private static List<TopRow> TopGroups(ReadOnlySpan<ProcessStats> processes, ProcessField field, int count)
    {
        var unit = MetricCatalog.Get(field).Unit;
        var groups = new Dictionary<string, (double Value, int Instances, double Cpu, double WorkingSet)>(StringComparer.OrdinalIgnoreCase);
        foreach (ref readonly var p in processes)
        {
            if (p.Pid == 0)
                continue;
            groups.TryGetValue(p.Name, out var g);
            groups[p.Name] = (g.Value + p.GetValue(field), g.Instances + 1, g.Cpu + p.Cpu, g.WorkingSet + p.WorkingSet);
        }
        return groups.OrderByDescending(g => g.Value.Value).Take(count)
            .Select(g => new TopRow(g.Key, null, g.Value.Instances, AgentUnits.Convert(unit, g.Value.Value), null, null, R(g.Value.Cpu), AgentUnits.Mb(g.Value.WorkingSet)))
            .ToList();
    }

    private static ProcessInstance Instance(in ProcessStats p) => new(
        p.Pid, p.ParentPid, p.Name, p.CreateTime > 0 ? AgentUnits.DateTime(DateTime.FromFileTimeUtc(p.CreateTime)) : null,
        R(p.Cpu), R(p.CpuKernel), AgentUnits.Mb(p.WorkingSet), AgentUnits.Mb(p.PrivateBytes), p.Handles, p.Threads,
        AgentUnits.Mb(p.IoBytes), AgentUnits.Mb(p.DiskReadBytes + p.DiskWriteBytes), AgentUnits.Mb(p.NetSendBytes + p.NetRecvBytes),
        R(p.HardFaults), R(p.GpuPercent), p.BasePriority, p.SessionId);
}

// --- Results (serialized camelCase; null members omitted) ----------------------------------------------------------

public sealed record Overview(
    string Now, string Machine, string Os, int LogicalProcessors, double SampleIntervalSeconds, double ScanIntervalSeconds,
    double HistoryMinutes, double HistoryRetentionMinutes, string KernelEvents, SystemNow Current, IReadOnlyList<WindowStats> Recent,
    IReadOnlyList<GpuNow> Gpus, IReadOnlyList<ProcessNow> TopCpu, IReadOnlyList<ProcessNow> TopMemory);

/// <summary>CPU values in logical CPUs (1.0 = one logical processor busy).</summary>
public sealed record SystemNow(
    double Cpu, double CpuKernel, double CpuDpc, double CpuInterrupt, double MemoryUsedMB, double MemoryTotalMB, double CommitMB,
    double CommitLimitMB, double StandbyMB, double FreeMB, double DiskReadMBps, double DiskWriteMBps, double DiskLatencyMs,
    double NetSendMBps, double NetRecvMBps, int Processes, int Threads, int Handles, double ContextSwitchesPerSec,
    double ProcessorQueue, double LongestDpcMs, double LongestIsrMs, double TimerResolutionMs, double CpuFrequencyMHz);

public sealed record WindowStats(
    double Minutes, double CpuAverage, double CpuMax, string CpuMaxAt, double MemoryUsedMaxMB, double DiskMBpsAverage,
    double DiskMBpsMax, double NetMBpsAverage, double NetMBpsMax, double LongestDpcMs, string LongestDpcAt);

public sealed record GpuNow(string Name, double? UtilizationPercent, double? DedicatedUsedMB, double? DedicatedTotalMB, double? TemperatureC, double? PowerW);

/// <summary><paramref name="Value"/> is the requested field, in its agent unit.</summary>
public sealed record ProcessNow(string Name, int Pid, double Value, double Cpu, double WorkingSetMB);

public sealed record MetricHistory(
    string Metric, string Series, string Unit, string From, string To, double SecondsPerPoint, string Resolution, SeriesStats Stats,
    IReadOnlyList<HistoryPoint> Points);

public sealed record SeriesStats(double Min, double Average, double Max, string MaxAt, double P95, double Last);

/// <summary>One point: the average of its samples, with their min and max when it covers more than one.</summary>
public sealed record HistoryPoint(string T, double Avg, double? Min, double? Max);

public sealed record TopProcesses(string Field, string Unit, string? From, string? To, string Source, IReadOnlyList<TopRow> Rows);

public sealed record TopRow(string Name, int? Pid, int? Instances, double Value, double? Max, string? MaxAt, double? Cpu, double? WorkingSetMB);

public sealed record ProcessDetails(string Pattern, int Instances, string? From, string? To, IReadOnlyList<ProcessInstance> Current, IReadOnlyList<FieldSummary> History);

public sealed record ProcessInstance(
    int Pid, int ParentPid, string Name, string? Started, double Cpu, double CpuKernel, double WorkingSetMB, double PrivateMB, int Handles,
    int Threads, double IoMBps, double DiskMBps, double NetMBps, double HardFaultsPerSec, double GpuPercent, int BasePriority, int Session);

public sealed record FieldSummary(string Metric, string Unit, double Average, double Max, string MaxAt, double Last);

public sealed record KernelActivity(
    string KernelEvents, string? From, string To, KernelNow System, IReadOnlyList<DriverRow> Drivers, IReadOnlyList<PoolTagRow> LargestPoolTags,
    IReadOnlyList<PoolTagRow> GrowingPoolTags, IReadOnlyList<ObjectTypeRow> GrowingObjectTypes);

/// <summary>DPC/interrupt CPU in logical CPUs; "InWindow" values are the maxima over the requested minutes.</summary>
public sealed record KernelNow(
    double CpuDpc, double CpuInterrupt, double DpcPerSec, double IsrPerSec, double LongestDpcMs, double LongestIsrMs,
    double LongestDpcInWindowMs, string? LongestDpcAt, double LongestIsrInWindowMs, string? LongestIsrAt, double NonPagedPoolMB,
    double PagedPoolMB, double InterruptsPerSec, double ContextSwitchesPerSec, double SystemCallsPerSec);

/// <summary>Times in ms of CPU per second (1000 = one CPU), over the last scan.</summary>
public sealed record DriverRow(string Name, double DpcMsPerSec, double DpcPerSec, double LongestDpcMs, double IsrMsPerSec, double IsrPerSec, double LongestIsrMs);

public sealed record PoolTagRow(string Tag, double NonPagedMB, double PagedMB, double GrowthKBps, double AllocationsPerSec, long Outstanding);

public sealed record ObjectTypeRow(string Name, long Objects, long Handles, double ObjectGrowthPerMin, double HandleGrowthPerMin);

public sealed record ProcessEvents(string KernelEvents, bool CommandLinesShared, IReadOnlyList<ProcessEventRow> Events);

public sealed record ProcessEventRow(string Time, string Kind, int Pid, int ParentPid, string Name, int? ExitCode, string? CommandLine);
