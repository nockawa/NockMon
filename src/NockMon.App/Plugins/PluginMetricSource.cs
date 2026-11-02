using NockMon.Core.Metrics;
using NockMon.Sdk;

namespace NockMon.Plugins;

/// <summary>A plugin's <see cref="IMetricPlugin"/> as the sampling engine sees it (Core knows nothing of the SDK).</summary>
internal sealed class PluginMetricSource(string pluginId, IMetricPlugin plugin) : IExternalMetricSource
{
    private IReadOnlyList<PluginMetric>? _from;
    private IReadOnlyList<ExternalMetric> _metrics = [];

    public IMetricPlugin Plugin => plugin;

    string IExternalMetricSource.Plugin => pluginId;

    public string Source => plugin.Id;

    public string DisplayName => plugin.DisplayName;

    /// <summary>Converted once per list the plugin returns (the engine compares references).</summary>
    public IReadOnlyList<ExternalMetric> Metrics
    {
        get
        {
            var current = plugin.Metrics;
            if (!ReferenceEquals(current, _from))
            {
                _metrics = current.Select(m => new ExternalMetric(m.Id, m.Name, m.ShortName, Convert(m.Unit), m.Group)).ToArray();
                _from = current;
            }
            return _metrics;
        }
    }

    public bool TryRead(IReadOnlyList<ExternalMetric> metrics, Span<double> values) =>
        ReferenceEquals(metrics, _metrics) && _from is { } from && plugin.TryRead(from, values);

    private static MetricUnit Convert(PluginMetricUnit unit) => unit switch
    {
        PluginMetricUnit.Percent => MetricUnit.Percent,
        PluginMetricUnit.Celsius => MetricUnit.Celsius,
        PluginMetricUnit.Rpm => MetricUnit.Rpm,
        PluginMetricUnit.Volts => MetricUnit.Volts,
        PluginMetricUnit.Amperes => MetricUnit.Amperes,
        PluginMetricUnit.Watts => MetricUnit.Watts,
        PluginMetricUnit.Megahertz => MetricUnit.Megahertz,
        PluginMetricUnit.Milliseconds => MetricUnit.Milliseconds,
        PluginMetricUnit.Bytes => MetricUnit.Bytes,
        PluginMetricUnit.BytesPerSecond => MetricUnit.BytesPerSecond,
        PluginMetricUnit.PerSecond => MetricUnit.PerSecond,
        _ => MetricUnit.Count,
    };
}

/// <summary>What the host gives a metric source.</summary>
internal sealed class PluginContext(string pluginId) : IPluginContext
{
    public string DataDirectory
    {
        get
        {
            var directory = PluginRuntime.DataDirectory(pluginId);
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public Version HostVersion => PluginRuntime.HostVersion;
}
