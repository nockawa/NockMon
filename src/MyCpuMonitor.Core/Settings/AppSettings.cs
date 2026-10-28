using System.Text.Json.Serialization;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.Settings;

public sealed class AppSettings
{
    /// <summary>Fast rate: system, per-CPU and watched processes.</summary>
    public int SampleIntervalMs { get; set; } = 500;

    /// <summary>Full process scan rate (top-N tables, per-name history). Rounded to a multiple of the sample interval.</summary>
    public int ScanIntervalMs { get; set; } = 2000;

    public int HistoryMinutes { get; set; } = 60;
    public CpuScale CpuScale { get; set; } = CpuScale.Machine;
    public bool AlwaysOnTop { get; set; }
    public string ActiveLayout { get; set; } = "Default";

    /// <summary>Sampling options from these settings, clamped to valid values.</summary>
    [JsonIgnore]
    public SamplingOptions SamplingOptions
    {
        get
        {
            var interval = TimeSpan.FromMilliseconds(Math.Max(SampleIntervalMs, SamplingOptions.MinInterval.TotalMilliseconds));
            var scan = TimeSpan.FromMilliseconds(Math.Max(ScanIntervalMs, interval.TotalMilliseconds));
            var retention = TimeSpan.FromMinutes(Math.Max(HistoryMinutes, 1));
            return new SamplingOptions(interval, scan, retention < scan * 2 ? scan * 2 : retention);
        }
    }
}
