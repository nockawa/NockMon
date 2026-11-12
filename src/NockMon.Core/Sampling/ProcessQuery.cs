using NockMon.Core.Metrics;
using NockMon.Core.Model;

namespace NockMon.Core.Sampling;

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
    string? Filter = null,
    bool ExcludeIdle = false);

/// <summary>
/// Filter, optional grouping by image name, sort and top-N over a snapshot. Owns its buffers:
/// allocation-free in steady state, results stay valid until the next <see cref="Run"/>.
/// </summary>
public sealed class ProcessQuery
{
    private readonly Dictionary<string, int> _groups = new(StringComparer.OrdinalIgnoreCase);
    private ProcessRow[] _rows = new ProcessRow[512];
    private ProcessRow[] _results = new ProcessRow[64];
    private SortKey[] _keys = new SortKey[512];
    private int[] _order = new int[512];
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
            if (options.ExcludeIdle && p.Pid == 0) // System Idle Process: idle time, not work.
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
        _resultCount = Math.Min(Math.Max(options.Top, 0), count);
        if (_results.Length < _resultCount)
            _results = new ProcessRow[Math.Max(_resultCount, _results.Length * 2)];
        if (options.SortBy == ProcessField.Name)
        {
            _sortField = options.SortBy;
            _sortDescending = options.Descending;
            rows.Sort(_comparison);
            rows[.._resultCount].CopyTo(_results);
            return Results;
        }

        // Numeric: sort 16-byte keys and indexes, not the rows (about 240 bytes each, copied by every swap), then copy
        // out the top ones. This runs under the engine lock, which the sampler waits on.
        if (_keys.Length < count)
        {
            _keys = new SortKey[_rows.Length];
            _order = new int[_rows.Length];
        }
        var keys = _keys.AsSpan(0, count);
        var order = _order.AsSpan(0, count);
        for (var i = 0; i < count; i++)
        {
            var value = rows[i].GetValue(options.SortBy);
            keys[i] = new SortKey(options.Descending ? -value : value, rows[i].Stats.Pid);
            order[i] = i;
        }
        keys.Sort(order);
        for (var i = 0; i < _resultCount; i++)
            _results[i] = rows[order[i]];
        return Results;
    }

    /// <summary>Sort value (negated for a descending sort), ties by PID for a stable display.</summary>
    private readonly record struct SortKey(double Value, int Pid) : IComparable<SortKey>
    {
        public int CompareTo(SortKey other)
        {
            var c = Value.CompareTo(other.Value);
            return c != 0 ? c : Pid.CompareTo(other.Pid);
        }
    }

    /// <summary>Rows of the last <see cref="Run"/>. Owned by this query: safe to read after the engine lock is released.</summary>
    public ReadOnlySpan<ProcessRow> Results => _results.AsSpan(0, _resultCount);

    private NamePattern? GetFilter(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;
        if (_filter?.Pattern != pattern)
            _filter = new NamePattern(pattern);
        return _filter;
    }

    /// <summary>
    /// Sums rates and sizes (the longest GC pause is the longest of the instances). Identity (PID, create time, parent) is the earliest-started instance's, together: the pair
    /// stays a real process key (icons, actions), whatever order the scan lists the processes in.
    /// </summary>
    private static void Accumulate(ref ProcessRow row, in ProcessStats p)
    {
        ref var s = ref row.Stats;
        row.Instances++;
        if (p.CreateTime < s.CreateTime)
            (s.Pid, s.CreateTime, s.ParentPid) = (p.Pid, p.CreateTime, p.ParentPid);
        var ops = s.DiskOps + p.DiskOps;
        s.DiskLatencyMs = ops > 0 ? (s.DiskLatencyMs * s.DiskOps + p.DiskLatencyMs * p.DiskOps) / ops : 0; // Weighted by I/Os.
        s.DiskOps = ops;
        s.DiskReadBytes += p.DiskReadBytes;
        s.DiskWriteBytes += p.DiskWriteBytes;
        s.NetSendBytes += p.NetSendBytes;
        s.NetRecvBytes += p.NetRecvBytes;
        s.GpuPercent += p.GpuPercent;
        s.GcPausePercent += p.GcPausePercent;
        s.GcPauseMaxMs = Math.Max(s.GcPauseMaxMs, p.GcPauseMaxMs);
        s.GcRate += p.GcRate;
        s.Gen2GcRate += p.Gen2GcRate;
        s.ManagedHeap += p.ManagedHeap;
        s.AllocationRate += p.AllocationRate;
        s.FreedRate += p.FreedRate;
        s.PromotedRate += p.PromotedRate;
        s.ExceptionRate += p.ExceptionRate;
        s.ContentionRate += p.ContentionRate;
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
