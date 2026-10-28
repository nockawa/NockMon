using MyCpuMonitor.Core.Metrics;

namespace MyCpuMonitor.Core.Layout;

public enum WidgetKind
{
    ProcessTable,
    Chart,
    Value,
}

/// <summary>Widget kinds the user can add, with their default and minimum sizes in cells.</summary>
public static class WidgetCatalog
{
    public static IReadOnlyList<WidgetKind> Kinds { get; } = Enum.GetValues<WidgetKind>();

    public static WidgetKind KindOf(WidgetConfig config) => config switch
    {
        ProcessTableWidgetConfig => WidgetKind.ProcessTable,
        ChartWidgetConfig => WidgetKind.Chart,
        ValueWidgetConfig => WidgetKind.Value,
        _ => throw new ArgumentException($"Unknown widget {config.GetType().Name}.", nameof(config)),
    };

    public static string DisplayName(WidgetKind kind) => kind switch
    {
        WidgetKind.ProcessTable => "Process table",
        WidgetKind.Chart => "Chart",
        WidgetKind.Value => "Value",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static GridSize DefaultSize(WidgetKind kind) => kind switch
    {
        WidgetKind.ProcessTable => new(10, 8),
        WidgetKind.Chart => new(6, 3),
        WidgetKind.Value => new(2, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static GridSize MinSize(WidgetKind kind) => kind switch
    {
        WidgetKind.ProcessTable => new(3, 2),
        WidgetKind.Chart => new(2, 1),
        WidgetKind.Value => new(1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static GridSize MinSize(WidgetConfig config) => MinSize(KindOf(config));

    /// <summary>New widget with default settings, placed at <paramref name="bounds"/>.</summary>
    public static WidgetConfig Create(WidgetKind kind, GridRect bounds) => kind switch
    {
        WidgetKind.ProcessTable => new ProcessTableWidgetConfig { Title = "Top processes", Bounds = bounds },
        WidgetKind.Chart => new ChartWidgetConfig
        {
            Title = "CPU",
            Bounds = bounds,
            Series = [new ChartSeriesConfig { Source = new SystemSeries(SystemMetric.Cpu) }],
        },
        WidgetKind.Value => new ValueWidgetConfig { Title = "CPU", Bounds = bounds, Source = new SystemSeries(SystemMetric.Cpu) },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
