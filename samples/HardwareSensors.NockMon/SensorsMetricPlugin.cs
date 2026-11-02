using NockMon.Sdk;

namespace HardwareSensors.NockMon;

/// <summary>
/// The sensors as NockMon metrics: charts, value widgets and value lists can show them, NockMon keeps their history,
/// AI agents can read them. Ids are LibreHardwareMonitor's sensor identifiers ("/amdcpu/0/temperature/2"), stable
/// across sessions on the same machine.
/// </summary>
public sealed class SensorsMetricPlugin : IMetricPlugin
{
    public string Id => "sensors";

    public string DisplayName => "Hardware sensors";

    public void Start(IPluginContext context) => SensorService.Start();

    public IReadOnlyList<PluginMetric> Metrics => SensorService.Metrics;

    public bool TryRead(IReadOnlyList<PluginMetric> metrics, Span<double> values) => SensorService.TryRead(metrics, values);

    public void Dispose() => SensorService.Stop();
}
