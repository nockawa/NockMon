using NockMon.Core.Gpu;
using NockMon.Core.Metrics;

namespace NockMon.Core.Layout;

public static class LayoutPatterns
{
    // All pages, shown or not: hidden tabs keep their history complete.

    /// <summary>Process name patterns used by a layout's charts, values and value lists (to be sampled at the fast rate).</summary>
    public static IEnumerable<string> Watched(LayoutDocument layout) => Series(layout)
        .OfType<ProcessSeries>()
        .Select(s => s.Pattern)
        .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// GPU engines a layout needs polled (each ~0.3 ms per sample): engine series, utilization of adapters without NVML
    /// (busiest engine: all of them), and GPU engine widgets.
    /// </summary>
    public static IEnumerable<(string Adapter, string Pattern)> GpuEngines(LayoutDocument layout, IReadOnlyList<GpuAdapterInfo> adapters)
    {
        var result = new HashSet<(string, string)>();
        foreach (var series in Series(layout).OfType<GpuSeries>())
        {
            if (series.Metric == GpuMetric.Engine)
                result.Add((series.Adapter, series.Engine ?? "*"));
            else if (series.Metric == GpuMetric.Utilization && adapters.FirstOrDefault(a => a.Name == series.Adapter) is { HasNvml: false })
                result.Add((series.Adapter, "*"));
        }
        foreach (var widget in StackWidgetConfig.Leaves(layout.AllWidgets).OfType<GpuEnginesWidgetConfig>())
        {
            var adapter = widget.Adapter ?? adapters.FirstOrDefault()?.Name;
            if (adapter is not null)
                result.Add((adapter, widget.EnginePattern));
        }
        return result;
    }

    private static IEnumerable<SeriesRef> Series(LayoutDocument layout) => StackWidgetConfig.Leaves(layout.AllWidgets).SelectMany(w => w switch
    {
        ChartWidgetConfig c => c.Series.Select(s => s.Source),
        ValueWidgetConfig v => [v.Source],
        ValueListWidgetConfig l => l.Items,
        _ => [],
    });
}
