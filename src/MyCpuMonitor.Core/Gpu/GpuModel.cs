namespace MyCpuMonitor.Core.Gpu;

public enum GpuMetric
{
    /// <summary>NVIDIA (NVML): % of time a kernel was executing, CUDA included. Other GPUs: busiest engine.</summary>
    Utilization,
    /// <summary>Busiest engine matching the series' engine pattern (D3DKMT), e.g. "Compute*", "3D", "Video*".</summary>
    Engine,
    MemoryControllerLoad,
    EncoderLoad,
    DecoderLoad,
    DedicatedUsed,
    DedicatedTotal,
    SharedUsed,
    SharedTotal,
    Temperature,
    /// <summary>Board power in watts (NVML).</summary>
    Power,
    /// <summary>Power as reported by the graphics kernel, % (WDDM 2.4+ drivers).</summary>
    PowerPercent,
    FanPercent,
    FanRpm,
    CoreClock,
    MemoryClock,
}

/// <summary>An adapter as discovered at start. Engine names arrive asynchronously (PDH listing); until then "Engine N".</summary>
public sealed class GpuAdapterInfo(string name, long luid, int engineCount, bool hasNvml)
{
    public string Name { get; } = name;
    public long Luid { get; } = luid;
    public bool HasNvml { get; } = hasNvml;

    /// <summary>Unique engine display names, by engine index. Replaced as a whole when names are resolved.</summary>
    public volatile string[] Engines = Enumerable.Range(0, engineCount).Select(i => $"Engine {i}").ToArray();
}

/// <summary>One adapter's values for a sample. Engine utilization is NaN for engines not polled.</summary>
public sealed class GpuStats
{
    public static readonly int MetricCount = Enum.GetValues<GpuMetric>().Length;

    public string Name = "";
    public string[] EngineNames = [];
    public readonly double[] Metrics = new double[MetricCount];
    public double[] Engines = [];

    public double this[GpuMetric metric]
    {
        get => Metrics[(int)metric];
        set => Metrics[(int)metric] = value;
    }
}

/// <summary>GPU data source. Called from the sampler thread only (except <see cref="Adapters"/>).</summary>
public interface IGpuSource : IDisposable
{
    IReadOnlyList<GpuAdapterInfo> Adapters { get; }

    /// <summary>Engines to poll (each engine query costs ~0.3 ms): (adapter name, engine name pattern).</summary>
    void SetWatchedEngines(IEnumerable<(string Adapter, string Pattern)> engines);

    /// <summary>Fills one <see cref="GpuStats"/> per adapter (list resized as needed, items reused).</summary>
    void Collect(List<GpuStats> target);

    /// <summary>Per-process GPU utilization (%) since the previous call, summed over GPUs (NVML only).</summary>
    void CollectProcesses(Dictionary<int, double> gpuByPid);
}
