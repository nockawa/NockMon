using System.Text.Json.Serialization;
using MyCpuMonitor.Core.Metrics;

namespace MyCpuMonitor.Core.Settings;

public sealed class AppSettings
{
    public int SampleIntervalMs { get; set; } = 1000;
    public int HistoryMinutes { get; set; } = 60;
    public CpuScale CpuScale { get; set; } = CpuScale.Machine;
    public bool AlwaysOnTop { get; set; }
    public string ActiveLayout { get; set; } = "Default";

    [JsonIgnore] public TimeSpan SampleInterval => TimeSpan.FromMilliseconds(SampleIntervalMs);
    [JsonIgnore] public TimeSpan HistoryRetention => TimeSpan.FromMinutes(HistoryMinutes);
}
