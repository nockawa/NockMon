using NockMon.Core.Metrics;
using NockMon.Core.Model;

namespace NockMon.Core.Sampling;

/// <summary>Per-pattern sums of <see cref="MetricCatalog.ProcessHistoryFields"/> for one fast tick.</summary>
public sealed class WatchedAggregate(string pattern)
{
    public string Pattern { get; } = pattern;
    public float[] Values { get; } = new float[MetricCatalog.ProcessHistoryFields.Length];
}

/// <summary>
/// Fast-rate sampling of the processes matching the watched name patterns (those used by charts and value
/// widgets), through query handles: ~15 µs per process instead of a full scan. Scans discover matching processes
/// (handles opened) and retire exited ones; each tick reads every handle and sums the rates per pattern.
/// Threads and hard faults are not available through a handle: their latest scan values are used.
/// Sampler thread only.
/// </summary>
public sealed class WatchedProcessTracker(ISystemProbe probe) : IDisposable
{
    private static readonly int CpuIndex = MetricCatalog.HistoryIndexOf(ProcessField.Cpu);
    private static readonly int PrivateIndex = MetricCatalog.HistoryIndexOf(ProcessField.PrivateBytes);
    private static readonly int WorkingSetIndex = MetricCatalog.HistoryIndexOf(ProcessField.WorkingSet);
    private static readonly int IoOpsIndex = MetricCatalog.HistoryIndexOf(ProcessField.IoOps);
    private static readonly int IoBytesIndex = MetricCatalog.HistoryIndexOf(ProcessField.IoBytes);
    private static readonly int HandlesIndex = MetricCatalog.HistoryIndexOf(ProcessField.Handles);
    private static readonly int ThreadsIndex = MetricCatalog.HistoryIndexOf(ProcessField.Threads);
    private static readonly int HardFaultsIndex = MetricCatalog.HistoryIndexOf(ProcessField.HardFaults);
    private static readonly int InstancesIndex = MetricCatalog.HistoryIndexOf(ProcessField.Instances);

    private readonly Dictionary<ProcessKey, Entry> _entries = new();
    private readonly List<ProcessKey> _retired = [];
    private WatchedAggregate[] _aggregates = [];
    private NamePattern[] _patterns = [];
    private long _scanGeneration;

    private sealed class Entry
    {
        public nint Handle;
        public ulong PatternMask;
        public RawProcess Previous;
        public TimeSpan PreviousElapsed;
        public bool HasPrevious;
        public int Threads;
        public double HardFaults;
        public long ScanGeneration;

        /// <summary>Scan rates contributed on the first tick, before the handle has a baseline of its own.</summary>
        public ProcessStats? FirstRates;
    }

    /// <summary>Results of the last <see cref="Tick"/>, one per pattern in <see cref="SetPatterns"/> order.</summary>
    public IReadOnlyList<WatchedAggregate> Aggregates => _aggregates;

    /// <summary>Processes currently watched (handles open).</summary>
    public int WatchedCount => _entries.Count;

    /// <summary>Replaces the watched patterns (at most 64). Handles are reopened at the next scan.</summary>
    public void SetPatterns(IEnumerable<string> patterns)
    {
        var distinct = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).Take(64).ToArray();
        CloseAll();
        _patterns = distinct.Select(p => new NamePattern(p)).ToArray();
        _aggregates = distinct.Select(p => new WatchedAggregate(p)).ToArray();
    }

    /// <summary>
    /// After a full scan: opens handles on newly matching processes, refreshes the scan-only values, and closes
    /// handles of processes absent from the scan.
    /// </summary>
    /// <param name="table">Rates of this scan, or null if the scan was a baseline.</param>
    public void OnScan(RawSample raw, ProcessTable? table)
    {
        if (_patterns.Length == 0)
            return;
        _scanGeneration++;

        for (var i = 0; i < raw.ProcessCount; i++)
        {
            ref readonly var p = ref raw.Processes[i];
            var key = new ProcessKey(p.Pid, p.CreateTime);
            if (!_entries.TryGetValue(key, out var entry))
            {
                var mask = Match(p.Name);
                if (mask == 0 || !probe.TryOpenProcess(key, out var handle))
                    continue;
                entry = new Entry { Handle = handle, PatternMask = mask, Previous = p };
                _entries.Add(key, entry);
            }
            entry.ScanGeneration = _scanGeneration;
            entry.Threads = p.Threads;
        }

        if (table is not null)
        {
            foreach (ref readonly var s in table.Span)
            {
                if (!_entries.TryGetValue(s.Key, out var entry))
                    continue;
                entry.HardFaults = s.HardFaults;
                if (!entry.HasPrevious)
                    entry.FirstRates = s;
            }
        }

        foreach (var (key, entry) in _entries)
        {
            if (entry.ScanGeneration != _scanGeneration)
                Retire(key, entry);
        }
        FlushRetired();
    }

    /// <summary>Reads every watched process and recomputes <see cref="Aggregates"/>.</summary>
    /// <remarks>
    /// Each read is timestamped individually: on scan ticks the reads happen after the scan, not at the tick time.
    /// A new process has no handle baseline yet: on its first tick it contributes the rates of the scan that found it.
    /// </remarks>
    public void Tick()
    {
        foreach (var aggregate in _aggregates)
            Array.Clear(aggregate.Values);

        foreach (var (key, entry) in _entries)
        {
            var current = entry.Previous;
            var now = probe.Now;
            if (!probe.TryReadProcess(entry.Handle, ref current))
            {
                Retire(key, entry);
                continue;
            }

            var dt = now - entry.PreviousElapsed;
            if (entry.HasPrevious && dt > TimeSpan.Zero)
            {
                var seconds = dt.TotalSeconds;
                var cpu = (SampleProcessor.Delta(current.UserTime, entry.Previous.UserTime)
                           + SampleProcessor.Delta(current.KernelTime, entry.Previous.KernelTime)) / dt.Ticks;
                var ioOps = (SampleProcessor.Delta(current.ReadOps, entry.Previous.ReadOps)
                             + SampleProcessor.Delta(current.WriteOps, entry.Previous.WriteOps)
                             + SampleProcessor.Delta(current.OtherOps, entry.Previous.OtherOps)) / seconds;
                var ioBytes = (SampleProcessor.Delta(current.ReadBytes, entry.Previous.ReadBytes)
                               + SampleProcessor.Delta(current.WriteBytes, entry.Previous.WriteBytes)
                               + SampleProcessor.Delta(current.OtherBytes, entry.Previous.OtherBytes)) / seconds;
                Accumulate(entry, in current, cpu, ioOps, ioBytes);
            }
            else if (entry.FirstRates is { } scan)
            {
                Accumulate(entry, in current, scan.Cpu, scan.IoOps, scan.IoBytes);
            }

            entry.FirstRates = null;
            entry.Previous = current;
            entry.PreviousElapsed = now;
            entry.HasPrevious = true;
        }
        FlushRetired();
    }

    private void Accumulate(Entry entry, in RawProcess current, double cpu, double ioOps, double ioBytes)
    {
        for (var i = 0; i < _aggregates.Length; i++)
        {
            if ((entry.PatternMask & (1UL << i)) == 0)
                continue;
            var v = _aggregates[i].Values;
            v[CpuIndex] += (float)cpu;
            v[PrivateIndex] += current.PrivateBytes;
            v[WorkingSetIndex] += current.WorkingSet;
            v[IoOpsIndex] += (float)ioOps;
            v[IoBytesIndex] += (float)ioBytes;
            v[HandlesIndex] += current.Handles;
            v[ThreadsIndex] += entry.Threads;
            v[HardFaultsIndex] += (float)entry.HardFaults;
            v[InstancesIndex] += 1;
        }
    }

    private ulong Match(string name)
    {
        ulong mask = 0;
        for (var i = 0; i < _patterns.Length; i++)
        {
            if (_patterns[i].IsMatch(name))
                mask |= 1UL << i;
        }
        return mask;
    }

    private void Retire(ProcessKey key, Entry entry)
    {
        probe.CloseProcess(entry.Handle);
        _retired.Add(key);
    }

    private void FlushRetired()
    {
        foreach (var key in _retired)
            _entries.Remove(key);
        _retired.Clear();
    }

    private void CloseAll()
    {
        foreach (var entry in _entries.Values)
            probe.CloseProcess(entry.Handle);
        _entries.Clear();
    }

    public void Dispose() => CloseAll();
}
