using NockMon.Core.Metrics;
using NockMon.Core.Model;
using NockMon.Core.Sampling;

namespace NockMon.Core.History;

/// <summary>
/// Two-rate history. Fast rings (one slot per sample): timestamps, system and per-CPU series, and watched-pattern
/// process series. Slow rings (one slot per process scan): process series aggregated per image name (sum over
/// instances), so they survive restarts, match any pattern and cover processes nobody watched yet.
/// <para>
/// Every series reads at the fast rate. A process series is the slow per-name sum upsampled as a step function
/// (a scan's value holds until the next scan), overlaid with the watched-pattern data where it exists: a chart added
/// now shows the past at scan resolution and the future at sample resolution.
/// </para>
/// A name series is evicted once it has had no instance for a full slow window. Absent = 0 (sum over no instance).
/// Not thread-safe: the engine serializes access.
/// </summary>
public sealed class HistoryStore
{
    private static readonly int SystemMetricCount = Enum.GetValues<SystemMetric>().Length;
    private static readonly int CoreMetricCount = Enum.GetValues<CoreMetric>().Length;
    private static readonly int ProcessFieldCount = MetricCatalog.ProcessHistoryFields.Length;

    private readonly DateTime[] _time;
    private readonly float[][] _system;
    private readonly float[][] _cores;
    private readonly long[] _scanSampleIndex;
    private readonly Dictionary<string, NameSeries> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WatchedSeries> _watched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NamePattern> _patterns = new();
    private readonly HashSet<string> _seenPatterns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (float[] Objects, float[] Handles)> _objectTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GpuRings> _gpus = new(StringComparer.OrdinalIgnoreCase);
    private float[] _gpuScratch = [];
    private float[] _scanScratch = [];
    private float[] _scratch = [];

    private sealed class NameSeries(string name, int capacity)
    {
        public readonly string Name = name;
        public readonly float[][] Values = CreateBuffers(ProcessFieldCount, capacity);
        public long LastActiveScan;
    }

    private sealed class WatchedSeries(int capacity)
    {
        public readonly float[][] Values = CreateBuffers(ProcessFieldCount, capacity);

        /// <summary>Sample index of the first value (older slots hold nothing).</summary>
        public long FirstSample;
    }

    public HistoryStore(int capacity, int scanCapacity, int coreCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(scanCapacity, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(coreCount);
        Capacity = capacity;
        ScanCapacity = scanCapacity;
        CoreCount = coreCount;
        _time = new DateTime[capacity];
        _system = CreateBuffers(SystemMetricCount, capacity);
        _cores = CreateBuffers(CoreMetricCount * coreCount, capacity);
        _scanSampleIndex = new long[scanCapacity];
    }

    /// <summary>Fast ring size (samples).</summary>
    public int Capacity { get; }

    /// <summary>Slow ring size (scans).</summary>
    public int ScanCapacity { get; }

    public int CoreCount { get; }

    /// <summary>Total samples ever appended (index of the next sample).</summary>
    public long TotalSamples { get; private set; }

    /// <summary>Total scans ever appended.</summary>
    public long TotalScans { get; private set; }

    /// <summary>Samples currently available.</summary>
    public int Count => (int)Math.Min(TotalSamples, Capacity);

    private int ScanCount => (int)Math.Min(TotalScans, ScanCapacity);

    /// <summary>Image names currently held in history.</summary>
    public IEnumerable<string> ProcessNames => _names.Keys;

    /// <summary>Kernel object type names seen in scans.</summary>
    public IEnumerable<string> ObjectTypeNames => _objectTypes.Keys;

    /// <summary>Appends a fast sample: system, cores, and the watched-pattern aggregates of this tick.</summary>
    public void AppendSample(Snapshot snapshot, IReadOnlyList<WatchedAggregate> watched)
    {
        if (snapshot.Cores.Length != CoreCount)
            throw new ArgumentException($"Snapshot has {snapshot.Cores.Length} cores, history expects {CoreCount}.", nameof(snapshot));

        var slot = (int)(TotalSamples % Capacity);
        _time[slot] = snapshot.WallTimeUtc;

        for (var m = 0; m < SystemMetricCount; m++)
            _system[m][slot] = (float)snapshot.System.GetValue((SystemMetric)m);

        for (var m = 0; m < CoreMetricCount; m++)
        {
            for (var c = 0; c < CoreCount; c++)
                _cores[m * CoreCount + c][slot] = (float)snapshot.Cores[c].GetValue((CoreMetric)m);
        }

        _seenPatterns.Clear();
        foreach (var aggregate in watched)
        {
            if (!_watched.TryGetValue(aggregate.Pattern, out var series))
                _watched[aggregate.Pattern] = series = new WatchedSeries(Capacity) { FirstSample = TotalSamples };
            for (var f = 0; f < ProcessFieldCount; f++)
                series.Values[f][slot] = aggregate.Values[f];
            _seenPatterns.Add(aggregate.Pattern);
        }
        foreach (var pattern in _watched.Keys)
        {
            if (!_seenPatterns.Contains(pattern))
                _watched.Remove(pattern); // No longer watched: its fast data would go stale.
        }

        foreach (var gpu in snapshot.Gpus)
        {
            if (!_gpus.TryGetValue(gpu.Name, out var rings) || rings.Engines.Length != gpu.Engines.Length)
                _gpus[gpu.Name] = rings = new GpuRings(Capacity, gpu.Engines.Length);
            rings.EngineNames = gpu.EngineNames;
            for (var m = 0; m < Gpu.GpuStats.MetricCount; m++)
                rings.Metrics[m][slot] = (float)gpu.Metrics[m];
            for (var e = 0; e < gpu.Engines.Length; e++)
                rings.Engines[e][slot] = (float)gpu.Engines[e];
        }

        TotalSamples++;
    }

    private sealed class GpuRings(int capacity, int engines)
    {
        public readonly float[][] Metrics = CreateBuffers(Gpu.GpuStats.MetricCount, capacity);
        public readonly float[][] Engines = CreateBuffers(engines, capacity);
        public string[] EngineNames = [];
    }

    /// <summary>
    /// GPU series: a metric ring, or for <see cref="Gpu.GpuMetric.Engine"/> the busiest engine matching the pattern per
    /// sample. Unavailable values (NaN: metric not supported, engine not polled) read as 0.
    /// </summary>
    private void ReadGpu(GpuSeries series, Span<float> destination)
    {
        destination.Clear();
        if (!_gpus.TryGetValue(series.Adapter, out var rings))
            return;

        if (series.Metric != Gpu.GpuMetric.Engine)
        {
            CopyLatest(rings.Metrics[(int)series.Metric], TotalSamples, Capacity, destination);
        }
        else
        {
            if (!_patterns.TryGetValue(series.Engine ?? "*", out var pattern))
                _patterns[series.Engine ?? "*"] = pattern = new NamePattern(series.Engine ?? "*");
            if (_gpuScratch.Length < Capacity)
                _gpuScratch = new float[Capacity];
            var one = _gpuScratch.AsSpan(0, destination.Length);
            destination.Fill(float.NaN);
            for (var e = 0; e < rings.Engines.Length && e < rings.EngineNames.Length; e++)
            {
                if (!pattern.IsMatch(rings.EngineNames[e]))
                    continue;
                CopyLatest(rings.Engines[e], TotalSamples, Capacity, one);
                for (var i = 0; i < destination.Length; i++)
                {
                    if (!float.IsNaN(one[i]) && (float.IsNaN(destination[i]) || one[i] > destination[i]))
                        destination[i] = one[i];
                }
            }
        }

        for (var i = 0; i < destination.Length; i++)
        {
            if (!float.IsFinite(destination[i]))
                destination[i] = 0;
        }
    }

    /// <summary>Appends a process scan, attached to the last appended sample.</summary>
    public void AppendScan(ProcessTable table)
    {
        if (TotalSamples == 0)
            throw new InvalidOperationException("A scan is attached to a sample: append a sample first.");

        var slot = (int)(TotalScans % ScanCapacity);
        _scanSampleIndex[slot] = TotalSamples - 1;

        foreach (var series in _names.Values)
        {
            foreach (var buffer in series.Values)
                buffer[slot] = 0;
        }

        foreach (ref readonly var p in table.Span)
        {
            if (!_names.TryGetValue(p.Name, out var series))
            {
                series = new NameSeries(p.Name, ScanCapacity);
                _names.Add(p.Name, series);
            }
            series.LastActiveScan = TotalScans;
            for (var f = 0; f < ProcessFieldCount; f++)
                series.Values[f][slot] += (float)p.GetValue(MetricCatalog.ProcessHistoryFields[f]);
        }

        foreach (ref readonly var t in table.ObjectTypeSpan)
        {
            if (!_objectTypes.TryGetValue(t.Name, out var rings))
                _objectTypes[t.Name] = rings = (new float[ScanCapacity], new float[ScanCapacity]);
            rings.Objects[slot] = t.Objects;
            rings.Handles[slot] = t.Handles;
        }

        TotalScans++;

        foreach (var (name, series) in _names)
        {
            if (TotalScans - series.LastActiveScan > ScanCapacity)
                _names.Remove(name);
        }
    }

    /// <summary>Copies the latest samples of <paramref name="series"/>, oldest first, into <paramref name="destination"/>.</summary>
    /// <returns>Number of samples written: min(destination length, <see cref="Count"/>).</returns>
    public int Read(SeriesRef series, Span<float> destination)
    {
        var n = Math.Min(destination.Length, Count);
        var dst = destination[..n];
        switch (series)
        {
            case SystemSeries s:
                CopyLatest(_system[(int)s.Metric], TotalSamples, Capacity, dst);
                break;
            case CoreSeries s:
                if ((uint)s.Core >= (uint)CoreCount)
                    throw new ArgumentOutOfRangeException(nameof(series), $"CPU {s.Core} does not exist ({CoreCount} CPUs).");
                CopyLatest(_cores[(int)s.Metric * CoreCount + s.Core], TotalSamples, Capacity, dst);
                break;
            case ProcessSeries s:
                ReadProcess(s, dst);
                break;
            case ObjectTypeSeries s:
                ReadObjectType(s, dst);
                break;
            case GpuSeries s:
                ReadGpu(s, dst);
                break;
            default:
                throw new ArgumentException($"Unsupported series {series}.", nameof(series));
        }
        return n;
    }

    /// <summary>Wall-clock timestamps (UTC) aligned with <see cref="Read"/>.</summary>
    public int ReadTimestamps(Span<DateTime> destination)
    {
        var n = Math.Min(destination.Length, Count);
        CopyLatest(_time, TotalSamples, Capacity, destination[..n]);
        return n;
    }

    /// <summary>True if <paramref name="pattern"/> currently has fast-rate data.</summary>
    public bool IsWatched(string pattern) => _watched.ContainsKey(pattern);

    private void ReadProcess(ProcessSeries series, Span<float> destination)
    {
        var field = MetricCatalog.HistoryIndexOf(series.Field);
        if (field < 0)
            throw new ArgumentException($"Process field {series.Field} has no history.", nameof(series));

        var n = destination.Length;
        var firstSample = TotalSamples - n;
        UpsampleScans(series.Pattern, field, firstSample, destination);

        // Fields not available through process handles (ETW disk/network) stay at scan resolution.
        if (MetricCatalog.IsWatchedField(series.Field) && _watched.TryGetValue(series.Pattern, out var watched))
        {
            var from = (int)Math.Max(0, watched.FirstSample - firstSample);
            if (from < n)
                CopyLatest(watched.Values[field], TotalSamples, Capacity, destination[from..]);
        }
    }

    /// <summary>Sums matching name series per scan, then holds each scan's value until the next one.</summary>
    private void UpsampleScans(string patternText, int field, long firstSample, Span<float> destination)
    {
        var scans = ScanCount;
        if (!_patterns.TryGetValue(patternText, out var pattern))
            _patterns[patternText] = pattern = new NamePattern(patternText);

        EnsureScratch();
        var sums = _scanScratch.AsSpan(0, scans);
        var one = _scratch.AsSpan(0, scans);
        sums.Clear();
        foreach (var named in _names.Values)
        {
            if (!pattern.IsMatch(named.Name))
                continue;
            CopyLatest(named.Values[field], TotalScans, ScanCapacity, one);
            for (var i = 0; i < scans; i++)
                sums[i] += one[i];
        }
        HoldScans(sums, firstSample, destination);
    }

    private void ReadObjectType(ObjectTypeSeries series, Span<float> destination)
    {
        var scans = ScanCount;
        EnsureScratch();
        var values = _scanScratch.AsSpan(0, scans);
        if (_objectTypes.TryGetValue(series.TypeName, out var rings))
            CopyLatest(series.Metric == ObjectTypeMetric.Objects ? rings.Objects : rings.Handles, TotalScans, ScanCapacity, values);
        else
            values.Clear();
        HoldScans(values, TotalSamples - destination.Length, destination);
    }

    private void EnsureScratch()
    {
        if (_scanScratch.Length >= ScanCapacity)
            return;
        _scanScratch = new float[ScanCapacity];
        _scratch = new float[ScanCapacity];
    }

    /// <summary>Upsamples per-scan values (oldest first) to samples: each scan's value holds until the next scan; 0 before the first.</summary>
    private void HoldScans(ReadOnlySpan<float> perScan, long firstSample, Span<float> destination)
    {
        var scans = perScan.Length;
        if (scans == 0)
        {
            destination.Clear();
            return;
        }
        var sums = perScan;
        var firstScanSlot = TotalScans - scans;
        var k = -1; // Index (in sums) of the last scan taken at or before the current sample.
        for (var i = 0; i < destination.Length; i++)
        {
            var sample = firstSample + i;
            while (k + 1 < scans && _scanSampleIndex[(int)((firstScanSlot + k + 1) % ScanCapacity)] <= sample)
                k++;
            destination[i] = k >= 0 ? sums[k] : 0;
        }
    }

    private static void CopyLatest<T>(T[] ring, long total, int capacity, Span<T> destination)
    {
        var n = destination.Length;
        if (n == 0)
            return;
        var start = (int)((total - n) % capacity);
        var first = Math.Min(n, capacity - start);
        ring.AsSpan(start, first).CopyTo(destination);
        ring.AsSpan(0, n - first).CopyTo(destination[first..]);
    }

    private static float[][] CreateBuffers(int count, int capacity)
    {
        var buffers = new float[count][];
        for (var i = 0; i < count; i++)
            buffers[i] = new float[capacity];
        return buffers;
    }
}
