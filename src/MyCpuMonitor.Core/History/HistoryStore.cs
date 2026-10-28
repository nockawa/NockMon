using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Model;
using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.History;

/// <summary>
/// Fixed-capacity ring of samples. System and per-core series are preallocated; process series are
/// aggregated per image name (sum over instances) so they survive restarts and match name patterns.
/// A name series is evicted once it has had no instance for a full window. Absent = 0 (sum over no instance).
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
    private readonly Dictionary<string, NameSeries> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NamePattern> _patterns = new();

    private sealed class NameSeries(string name, int capacity)
    {
        public readonly string Name = name;
        public readonly float[][] Values = CreateBuffers(ProcessFieldCount, capacity);
        public long LastActive;
    }

    public HistoryStore(int capacity, int coreCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(coreCount);
        Capacity = capacity;
        CoreCount = coreCount;
        _time = new DateTime[capacity];
        _system = CreateBuffers(SystemMetricCount, capacity);
        _cores = CreateBuffers(CoreMetricCount * coreCount, capacity);
    }

    public int Capacity { get; }
    public int CoreCount { get; }

    /// <summary>Total samples ever appended (index of the next sample).</summary>
    public long TotalSamples { get; private set; }

    /// <summary>Samples currently available.</summary>
    public int Count => (int)Math.Min(TotalSamples, Capacity);

    /// <summary>Image names currently held in history.</summary>
    public IEnumerable<string> ProcessNames => _names.Keys;

    public void Append(Snapshot snapshot)
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

        foreach (var series in _names.Values)
        {
            foreach (var buffer in series.Values)
                buffer[slot] = 0;
        }

        foreach (ref readonly var p in snapshot.ProcessSpan)
        {
            if (!_names.TryGetValue(p.Name, out var series))
            {
                series = new NameSeries(p.Name, Capacity);
                _names.Add(p.Name, series);
            }
            series.LastActive = TotalSamples;
            for (var f = 0; f < ProcessFieldCount; f++)
                series.Values[f][slot] += (float)p.GetValue(MetricCatalog.ProcessHistoryFields[f]);
        }

        TotalSamples++;

        foreach (var (name, series) in _names)
        {
            if (TotalSamples - series.LastActive > Capacity)
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
                CopyLatest(_system[(int)s.Metric], dst);
                break;
            case CoreSeries s:
                if ((uint)s.Core >= (uint)CoreCount)
                    throw new ArgumentOutOfRangeException(nameof(series), $"Core {s.Core} does not exist ({CoreCount} cores).");
                CopyLatest(_cores[(int)s.Metric * CoreCount + s.Core], dst);
                break;
            case ProcessSeries s:
                ReadProcess(s, dst);
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
        CopyLatest(_time, destination[..n]);
        return n;
    }

    private void ReadProcess(ProcessSeries series, Span<float> destination)
    {
        var field = MetricCatalog.HistoryIndexOf(series.Field);
        if (field < 0)
            throw new ArgumentException($"Process field {series.Field} has no history.", nameof(series));

        if (!_patterns.TryGetValue(series.Pattern, out var pattern))
            _patterns[series.Pattern] = pattern = new NamePattern(series.Pattern);

        destination.Clear();
        Span<float> scratch = destination.Length <= 4096 ? stackalloc float[destination.Length] : new float[destination.Length];
        foreach (var named in _names.Values)
        {
            if (!pattern.IsMatch(named.Name))
                continue;
            CopyLatest(named.Values[field], scratch);
            for (var i = 0; i < destination.Length; i++)
                destination[i] += scratch[i];
        }
    }

    private void CopyLatest<T>(T[] ring, Span<T> destination)
    {
        var n = destination.Length;
        if (n == 0)
            return;
        var start = (int)((TotalSamples - n) % Capacity);
        var first = Math.Min(n, Capacity - start);
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
