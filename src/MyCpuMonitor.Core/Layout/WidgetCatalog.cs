using MyCpuMonitor.Core.Metrics;

namespace MyCpuMonitor.Core.Layout;

public enum WidgetKind
{
    ProcessTable,
    Chart,
    Value,
    CpuGrid,
    PoolTags,
    ValueList,
    Drivers,
    ProcessEvents,
    ObjectTypes,
    GpuEngines,

    /// <summary>Created by dropping a widget onto another, not from the add menu.</summary>
    Stack,
}

/// <summary>Widget kinds the user can add, with their default and minimum sizes in cells.</summary>
public static class WidgetCatalog
{
    /// <summary>Kinds offered by the add menu.</summary>
    public static IReadOnlyList<WidgetKind> Kinds { get; } = Enum.GetValues<WidgetKind>().Where(k => k != WidgetKind.Stack).ToArray();

    public static WidgetKind KindOf(WidgetConfig config) => config switch
    {
        ProcessTableWidgetConfig => WidgetKind.ProcessTable,
        ChartWidgetConfig => WidgetKind.Chart,
        ValueWidgetConfig => WidgetKind.Value,
        CpuGridWidgetConfig => WidgetKind.CpuGrid,
        PoolTagTableWidgetConfig => WidgetKind.PoolTags,
        ValueListWidgetConfig => WidgetKind.ValueList,
        DriverTableWidgetConfig => WidgetKind.Drivers,
        ProcessEventsWidgetConfig => WidgetKind.ProcessEvents,
        ObjectTypesWidgetConfig => WidgetKind.ObjectTypes,
        GpuEnginesWidgetConfig => WidgetKind.GpuEngines,
        StackWidgetConfig => WidgetKind.Stack,
        _ => throw new ArgumentException($"Unknown widget {config.GetType().Name}.", nameof(config)),
    };

    public static string DisplayName(WidgetKind kind) => kind switch
    {
        WidgetKind.ProcessTable => "Process table",
        WidgetKind.Chart => "Chart",
        WidgetKind.Value => "Value",
        WidgetKind.CpuGrid => "Per-CPU graphs",
        WidgetKind.PoolTags => "Kernel pool tags",
        WidgetKind.ValueList => "Value list",
        WidgetKind.Drivers => "Drivers (DPC/ISR)",
        WidgetKind.ProcessEvents => "Process events",
        WidgetKind.ObjectTypes => "Kernel objects",
        WidgetKind.GpuEngines => "GPU engines",
        WidgetKind.Stack => "Stack",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static GridSize DefaultSize(WidgetKind kind) => kind switch
    {
        WidgetKind.ProcessTable => new(10, 8),
        WidgetKind.Chart => new(6, 3),
        WidgetKind.Value => new(2, 2),
        WidgetKind.CpuGrid => new(8, 4),
        WidgetKind.PoolTags => new(7, 6),
        WidgetKind.ValueList => new(4, 7),
        WidgetKind.Drivers => new(8, 5),
        WidgetKind.ProcessEvents => new(10, 6),
        WidgetKind.ObjectTypes => new(8, 6),
        WidgetKind.GpuEngines => new(8, 4),
        WidgetKind.Stack => new(1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static GridSize MinSize(WidgetKind kind) => kind switch
    {
        WidgetKind.ProcessTable => new(3, 2),
        WidgetKind.Chart => new(2, 1),
        WidgetKind.Value => new(1, 1),
        WidgetKind.CpuGrid => new(2, 2),
        WidgetKind.PoolTags => new(3, 2),
        WidgetKind.ValueList => new(2, 1),
        WidgetKind.Drivers => new(3, 2),
        WidgetKind.ProcessEvents => new(3, 2),
        WidgetKind.ObjectTypes => new(3, 2),
        WidgetKind.GpuEngines => new(2, 2),
        WidgetKind.Stack => new(1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Minimum size of a widget; for a stack, the largest minimum of its items on each axis.</summary>
    public static GridSize MinSize(WidgetConfig config)
    {
        if (config is not StackWidgetConfig stack)
            return MinSize(KindOf(config));
        int columns = 1, rows = 1;
        foreach (var item in stack.Items)
        {
            var min = MinSize(item);
            (columns, rows) = (Math.Max(columns, min.Columns), Math.Max(rows, min.Rows));
        }
        return new GridSize(columns, rows);
    }

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
        WidgetKind.CpuGrid => new CpuGridWidgetConfig { Title = "CPUs", Bounds = bounds },
        WidgetKind.PoolTags => new PoolTagTableWidgetConfig { Title = "Kernel pool", Bounds = bounds },
        WidgetKind.ValueList => new ValueListWidgetConfig { Title = "Kernel", Bounds = bounds, Items = ValueListWidgetConfig.KernelOverview() },
        WidgetKind.Drivers => new DriverTableWidgetConfig { Title = "Drivers (DPC/ISR)", Bounds = bounds },
        WidgetKind.ProcessEvents => new ProcessEventsWidgetConfig { Title = "Process events", Bounds = bounds },
        WidgetKind.ObjectTypes => new ObjectTypesWidgetConfig { Title = "Kernel objects", Bounds = bounds },
        WidgetKind.GpuEngines => new GpuEnginesWidgetConfig { Title = "GPU engines", Bounds = bounds },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
