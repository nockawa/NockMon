using System.Diagnostics;
using MyCpuMonitor.Core.Model;

namespace MyCpuMonitor.Core.Gpu;

/// <summary>
/// GPU metrics for every adapter. Cheap every sample: memory segments, graphics-kernel perf data (temperature, fan,
/// power %, memory clock) and, for NVIDIA, NVML (utilization, encoder/decoder, clocks, watts). Engines (~0.3 ms each)
/// only when watched. Engine names are loaded once in the background.
/// </summary>
public sealed class GpuProbe : IGpuSource
{
    private const ulong UnlimitedSegment = 1UL << 50;

    private readonly List<AdapterState> _adapters = [];
    private readonly Nvml? _nvml;
    private (string Adapter, string Pattern)[] _watchedRequest = [];
    private volatile bool _resolveWatched = true;
    private DateTime _lastProcessQueryUtc = DateTime.UtcNow;

    private sealed class AdapterState(D3dkmt.Adapter adapter, GpuAdapterInfo info, uint segments, nint nvmlDevice)
    {
        public readonly D3dkmt.Adapter Adapter = adapter;
        public readonly GpuAdapterInfo Info = info;
        public readonly uint Segments = segments;
        public readonly nint NvmlDevice = nvmlDevice;
        public readonly long[] PreviousRunning = Enumerable.Repeat(-1L, info.Engines.Length).ToArray();
        public readonly long[] PreviousTimestamp = new long[info.Engines.Length];
        public bool[] Watched = new bool[info.Engines.Length];
    }

    public GpuProbe()
    {
        _nvml = Nvml.TryLoad();
        var nvmlDevices = _nvml?.Devices() ?? [];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in D3dkmt.EnumerateAdapters())
        {
            var (segments, nodes) = D3dkmt.Counts(adapter.Luid);
            var baseName = D3dkmt.Name(adapter.Handle) ?? "Unnamed adapter";
            var name = baseName;
            for (var n = 2; !used.Add(name); n++)
                name = $"{baseName} #{n}";

            var nvmlIndex = nvmlDevices.FindIndex(d => string.Equals(d.Name, baseName, StringComparison.OrdinalIgnoreCase));
            var device = nvmlIndex >= 0 ? nvmlDevices[nvmlIndex].Device : 0;
            if (nvmlIndex >= 0)
                nvmlDevices.RemoveAt(nvmlIndex);
            _adapters.Add(new AdapterState(adapter, new GpuAdapterInfo(name, adapter.Luid, (int)nodes, device != 0), segments, device));
        }

        Task.Run(LoadEngineNames);
    }

    public IReadOnlyList<GpuAdapterInfo> Adapters => _adapters.Select(a => a.Info).ToArray();

    public bool HasNvml => _nvml is not null;

    public void SetWatchedEngines(IEnumerable<(string Adapter, string Pattern)> engines)
    {
        _watchedRequest = engines.ToArray();
        _resolveWatched = true;
    }

    public void Collect(List<GpuStats> target)
    {
        if (_resolveWatched)
            ResolveWatched();
        while (target.Count < _adapters.Count)
            target.Add(new GpuStats());
        if (target.Count > _adapters.Count)
            target.RemoveRange(_adapters.Count, target.Count - _adapters.Count);

        for (var i = 0; i < _adapters.Count; i++)
            CollectAdapter(_adapters[i], target[i]);
    }

    public void CollectProcesses(Dictionary<int, double> gpuByPid)
    {
        gpuByPid.Clear();
        if (_nvml is null)
            return;
        var now = DateTime.UtcNow;
        // NVML keeps sparse per-process samples: look back past the previous call (overlap is harmless, max per PID).
        var since = (ulong)((_lastProcessQueryUtc.AddSeconds(-2) - DateTime.UnixEpoch).Ticks / 10);
        _lastProcessQueryUtc = now;
        var perDevice = new Dictionary<int, double>();
        foreach (var adapter in _adapters)
        {
            if (adapter.NvmlDevice == 0)
                continue;
            perDevice.Clear();
            _nvml.ProcessUtilization(adapter.NvmlDevice, since, perDevice);
            foreach (var (pid, value) in perDevice)
                gpuByPid[pid] = gpuByPid.GetValueOrDefault(pid) + value;
        }
    }

    private void CollectAdapter(AdapterState state, GpuStats stats)
    {
        stats.Name = state.Info.Name;
        stats.EngineNames = state.Info.Engines;
        if (stats.Engines.Length != stats.EngineNames.Length)
            stats.Engines = new double[stats.EngineNames.Length];
        Array.Fill(stats.Metrics, double.NaN);

        ulong dedicatedUsed = 0, dedicatedTotal = 0, sharedUsed = 0, sharedTotal = 0;
        for (uint s = 0; s < state.Segments; s++)
        {
            if (!D3dkmt.Segment(state.Info.Luid, s, out var limit, out var resident, out var aperture))
                continue;
            if (aperture)
            {
                sharedUsed += resident;
                if (limit < UnlimitedSegment)
                    sharedTotal += limit;
            }
            else
            {
                dedicatedUsed += resident;
                dedicatedTotal += limit;
            }
        }
        stats[GpuMetric.DedicatedUsed] = dedicatedUsed;
        stats[GpuMetric.DedicatedTotal] = dedicatedTotal;
        stats[GpuMetric.SharedUsed] = sharedUsed;
        stats[GpuMetric.SharedTotal] = sharedTotal;

        if (D3dkmt.PerfData(state.Adapter.Handle, out var memoryMhz, out var fanRpm, out var powerPercent, out var temperature))
        {
            stats[GpuMetric.MemoryClock] = memoryMhz;
            stats[GpuMetric.FanRpm] = fanRpm;
            stats[GpuMetric.PowerPercent] = powerPercent;
            stats[GpuMetric.Temperature] = temperature;
        }

        double busiest = double.NaN;
        for (var e = 0; e < stats.Engines.Length; e++)
        {
            stats.Engines[e] = double.NaN;
            if (e >= state.Watched.Length || !state.Watched[e])
                continue;
            var running = D3dkmt.NodeRunningTime(state.Info.Luid, (uint)e);
            var timestamp = Stopwatch.GetTimestamp();
            if (running >= 0 && state.PreviousRunning[e] >= 0)
            {
                var elapsed = Stopwatch.GetElapsedTime(state.PreviousTimestamp[e], timestamp).Ticks;
                if (elapsed > 0)
                {
                    stats.Engines[e] = Math.Clamp((running - state.PreviousRunning[e]) * 100.0 / elapsed, 0, 100);
                    busiest = double.IsNaN(busiest) ? stats.Engines[e] : Math.Max(busiest, stats.Engines[e]);
                }
            }
            state.PreviousRunning[e] = running;
            state.PreviousTimestamp[e] = timestamp;
        }

        if (_nvml is not null && state.NvmlDevice != 0)
        {
            var n = _nvml.Sample(state.NvmlDevice);
            stats[GpuMetric.Utilization] = n.Gpu;
            stats[GpuMetric.MemoryControllerLoad] = n.MemoryController;
            stats[GpuMetric.EncoderLoad] = n.Encoder;
            stats[GpuMetric.DecoderLoad] = n.Decoder;
            stats[GpuMetric.Power] = n.PowerW;
            stats[GpuMetric.FanPercent] = n.FanPercent;
            stats[GpuMetric.CoreClock] = n.CoreMhz;
            if (double.IsFinite(n.TemperatureC))
                stats[GpuMetric.Temperature] = n.TemperatureC;
            if (double.IsFinite(n.MemoryMhz))
                stats[GpuMetric.MemoryClock] = n.MemoryMhz;
        }
        else
        {
            stats[GpuMetric.Utilization] = busiest;
        }
    }

    private void ResolveWatched()
    {
        _resolveWatched = false;
        foreach (var state in _adapters)
        {
            var watched = new bool[state.Info.Engines.Length];
            var names = state.Info.Engines;
            foreach (var (adapter, pattern) in _watchedRequest)
            {
                if (!string.Equals(adapter, state.Info.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                var glob = new NamePattern(pattern);
                for (var e = 0; e < names.Length; e++)
                    watched[e] |= glob.IsMatch(names[e]);
            }
            state.Watched = watched;
        }
    }

    private void LoadEngineNames()
    {
        try
        {
            var names = GpuEngineNames.Load();
            foreach (var state in _adapters)
            {
                var engines = new string[state.Info.Engines.Length];
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var e = 0; e < engines.Length; e++)
                {
                    var baseName = names.GetValueOrDefault((state.Info.Luid, e), $"Engine {e}");
                    var name = baseName;
                    for (var n = 2; !used.Add(name); n++)
                        name = $"{baseName} #{n}";
                    engines[e] = name;
                }
                state.Info.Engines = engines;
            }
            _resolveWatched = true; // Patterns may now match by type name.
        }
        catch (Exception e)
        {
            Debug.WriteLine($"GPU engine names unavailable: {e.Message}");
        }
    }

    public void Dispose()
    {
        foreach (var state in _adapters)
            D3dkmt.Close(state.Adapter.Handle);
        _nvml?.Dispose();
    }
}
