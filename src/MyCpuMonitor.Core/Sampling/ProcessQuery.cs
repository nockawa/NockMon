using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Model;

namespace MyCpuMonitor.Core.Sampling;

/// <summary>A table row: one process, or the sum of all processes sharing an image name.</summary>
public struct ProcessRow
{
    public ProcessStats Stats;
    public int Instances;

    public readonly double GetValue(ProcessField field) =>
        field == ProcessField.Instances ? Instances : Stats.GetValue(field);
}

public readonly record struct ProcessQueryOptions(
    ProcessField SortBy,
    bool Descending,
    int Top,
    bool GroupByName = false,
    string? Filter = null);

/// <summary>
/// Filter, optional grouping by image name, sort and top-N over a snapshot. Owns its buffers:
/// allocation-free in steady state, results stay valid until the next <see cref="Run"/>.
/// </summary>
public sealed class ProcessQuery
{
    private readonly Dictionary<string, int> _groups = new(StringComparer.OrdinalIgnoreCase);
    private ProcessRow[] _rows = new ProcessRow[512];
    private NamePattern? _filter;
    private int _resultCount;
    private ProcessField _sortField;
    private bool _sortDescending;

    // Cached: Span.Sort with a struct comparer wraps it into a new delegate on every call.
    private readonly Comparison<ProcessRow> _comparison;

    public ProcessQuery() => _comparison = Compare;

    public ReadOnlySpan<ProcessRow> Run(Snapshot snapshot, in ProcessQueryOptions options)
    {
        var filter = GetFilter(options.Filter);
        if (_rows.Length < snapshot.ProcessCount)
            _rows = new ProcessRow[Math.Max(snapshot.ProcessCount, _rows.Length * 2)];

        var count = 0;
        _groups.Clear();
        foreach (ref readonly var p in snapshot.ProcessSpan)
        {
            if (filter is not null && !filter.IsMatch(p.Name))
                continue;

            if (options.GroupByName && _groups.TryGetValue(p.Name, out var index))
            {
                Accumulate(ref _rows[index], in p);
                continue;
            }
            if (options.GroupByName)
                _groups.Add(p.Name, count);
            _rows[count++] = new ProcessRow { Stats = p, Instances = 1 };
        }

        var rows = _rows.AsSpan(0, count);
        _sortField = options.SortBy;
        _sortDescending = options.Descending;
        rows.Sort(_comparison);
        _resultCount = Math.Min(Math.Max(options.Top, 0), count);
        return Results;
    }

    /// <summary>Rows of the last <see cref="Run"/>. Owned by this query: safe to read after the engine lock is released.</summary>
    public ReadOnlySpan<ProcessRow> Results => _rows.AsSpan(0, _resultCount);

    private NamePattern? GetFilter(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;
        if (_filter?.Pattern != pattern)
            _filter = new NamePattern(pattern);
        return _filter;
    }

    /// <summary>Sums rates and sizes; identity fields keep the first (lowest sorted) instance, start time the earliest.</summary>
    private static void Accumulate(ref ProcessRow row, in ProcessStats p)
    {
        ref var s = ref row.Stats;
        row.Instances++;
        s.CreateTime = Math.Min(s.CreateTime, p.CreateTime);
        var ops = s.DiskOps + p.DiskOps;
        s.DiskLatencyMs = ops > 0 ? (s.DiskLatencyMs * s.DiskOps + p.DiskLatencyMs * p.DiskOps) / ops : 0; // Weighted by I/Os.
        s.DiskOps = ops;
        s.DiskReadBytes += p.DiskReadBytes;
        s.DiskWriteBytes += p.DiskWriteBytes;
        s.NetSendBytes += p.NetSendBytes;
        s.NetRecvBytes += p.NetRecvBytes;
        s.Cpu += p.Cpu;
        s.CpuUser += p.CpuUser;
        s.CpuKernel += p.CpuKernel;
        s.WorkingSet += p.WorkingSet;
        s.PrivateWorkingSet += p.PrivateWorkingSet;
        s.PrivateBytes += p.PrivateBytes;
        s.VirtualSize += p.VirtualSize;
        s.PagedPool += p.PagedPool;
        s.NonPagedPool += p.NonPagedPool;
        s.IoReadOps += p.IoReadOps;
        s.IoWriteOps += p.IoWriteOps;
        s.IoOtherOps += p.IoOtherOps;
        s.IoReadBytes += p.IoReadBytes;
        s.IoWriteBytes += p.IoWriteBytes;
        s.IoOtherBytes += p.IoOtherBytes;
        s.PageFaults += p.PageFaults;
        s.HardFaults += p.HardFaults;
        s.Handles += p.Handles;
        s.Threads += p.Threads;
    }

    /// <summary>Ties broken by PID for a stable display.</summary>
    private int Compare(ProcessRow a, ProcessRow b)
    {
        var c = _sortField == ProcessField.Name
            ? string.Compare(a.Stats.Name, b.Stats.Name, StringComparison.OrdinalIgnoreCase)
            : a.GetValue(_sortField).CompareTo(b.GetValue(_sortField));
        if (_sortDescending)
            c = -c;
        return c != 0 ? c : a.Stats.Pid.CompareTo(b.Stats.Pid);
    }
}
