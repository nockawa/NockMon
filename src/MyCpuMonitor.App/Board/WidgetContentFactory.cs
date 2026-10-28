using Microsoft.UI.Xaml;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Monitoring;
using MyCpuMonitor.Widgets;

namespace MyCpuMonitor.Board;

public static class WidgetContentFactory
{
    public static UIElement Create(WidgetConfig config, MonitorService monitor) => config switch
    {
        ProcessTableWidgetConfig table => new ProcessTableWidget(table, monitor),
        ChartWidgetConfig chart => new ChartWidget(chart, monitor),
        ValueWidgetConfig value => new ValueWidget(value, monitor),
        CpuGridWidgetConfig grid => new CpuGridWidget(grid, monitor),
        PoolTagTableWidgetConfig pool => new PoolTagTableWidget(pool, monitor),
        ValueListWidgetConfig list => new ValueListWidget(list, monitor),
        _ => throw new ArgumentException($"Unknown widget {config.GetType().Name}.", nameof(config)),
    };
}
