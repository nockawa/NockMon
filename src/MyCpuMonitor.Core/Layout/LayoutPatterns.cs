using MyCpuMonitor.Core.Metrics;

namespace MyCpuMonitor.Core.Layout;

public static class LayoutPatterns
{
    /// <summary>Process name patterns used by a layout's charts and value widgets (to be sampled at the fast rate).</summary>
    public static IEnumerable<string> Watched(LayoutDocument layout) => layout.Widgets
        .SelectMany(w => w switch
        {
            ChartWidgetConfig c => c.Series.Select(s => s.Source),
            ValueWidgetConfig v => [v.Source],
            _ => [],
        })
        .OfType<ProcessSeries>()
        .Select(s => s.Pattern)
        .Distinct(StringComparer.Ordinal);
}
