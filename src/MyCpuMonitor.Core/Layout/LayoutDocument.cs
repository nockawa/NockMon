using System.Text.Json.Serialization;
using MyCpuMonitor.Core.Metrics;

namespace MyCpuMonitor.Core.Layout;

/// <summary>A named arrangement of widgets on the board. Persisted as JSON.</summary>
public sealed class LayoutDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public required string Name { get; set; }
    public BoardConfig Board { get; set; } = new();
    public List<WidgetConfig> Widgets { get; set; } = [];

    /// <summary>Pairs of widgets whose bounds overlap.</summary>
    public IEnumerable<(WidgetConfig A, WidgetConfig B)> FindCollisions()
    {
        for (var i = 0; i < Widgets.Count; i++)
        {
            for (var j = i + 1; j < Widgets.Count; j++)
            {
                if (Widgets[i].Bounds.Intersects(Widgets[j].Bounds))
                    yield return (Widgets[i], Widgets[j]);
            }
        }
    }

    public static LayoutDocument CreateDefault(string name = "Default") => new()
    {
        Name = name,
        Widgets =
        [
            new ProcessTableWidgetConfig { Title = "Top processes", Bounds = new GridRect(0, 0, 10, 8) },
            new ChartWidgetConfig
            {
                Title = "CPU",
                Bounds = new GridRect(10, 0, 8, 4),
                Series = [new ChartSeriesConfig { Source = new SystemSeries(SystemMetric.Cpu) }],
            },
            new ChartWidgetConfig
            {
                Title = "Memory",
                Bounds = new GridRect(10, 4, 8, 4),
                Series =
                [
                    new ChartSeriesConfig { Source = new SystemSeries(SystemMetric.PhysicalUsed) },
                    new ChartSeriesConfig { Source = new SystemSeries(SystemMetric.CommitTotal) },
                ],
            },
        ],
    };
}

public enum BoardOverflow
{
    /// <summary>Widgets beyond the window are hidden (kept in the layout).</summary>
    Hide,
    /// <summary>The board scrolls to show all widgets.</summary>
    Scroll,
}

public sealed class BoardConfig
{
    /// <summary>Cell size in effective pixels (DPI-independent).</summary>
    public double CellSize { get; set; } = 64;

    /// <summary>Gap between cells in effective pixels.</summary>
    public double Gap { get; set; } = 4;

    public BoardOverflow Overflow { get; set; } = BoardOverflow.Hide;
}

/// <summary>Position and size in board cells.</summary>
public readonly record struct GridRect(int Column, int Row, int ColumnSpan, int RowSpan)
{
    [JsonIgnore] public int Right => Column + ColumnSpan;
    [JsonIgnore] public int Bottom => Row + RowSpan;

    public bool Intersects(GridRect other) =>
        Column < other.Right && other.Column < Right && Row < other.Bottom && other.Row < Bottom;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ProcessTableWidgetConfig), "processTable")]
[JsonDerivedType(typeof(ChartWidgetConfig), "chart")]
[JsonDerivedType(typeof(ValueWidgetConfig), "value")]
public abstract class WidgetConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Title { get; set; }
    public GridRect Bounds { get; set; }
}

public sealed class ProcessTableWidgetConfig : WidgetConfig
{
    public int TopCount { get; set; } = 20;
    public ProcessField SortBy { get; set; } = ProcessField.Cpu;
    public bool Descending { get; set; } = true;

    /// <summary>Aggregate processes sharing an image name into one row.</summary>
    public bool GroupByName { get; set; }

    /// <summary>Optional name pattern; only matching processes are listed.</summary>
    public string? Filter { get; set; }

    public List<TableColumnConfig> Columns { get; set; } =
    [
        new() { Field = ProcessField.Name, Width = 180 },
        new() { Field = ProcessField.Pid, Width = 64 },
        new() { Field = ProcessField.Cpu, Width = 72 },
        new() { Field = ProcessField.PrivateBytes, Width = 88 },
        new() { Field = ProcessField.WorkingSet, Width = 88 },
        new() { Field = ProcessField.IoOps, Width = 80 },
    ];
}

public sealed class TableColumnConfig
{
    public ProcessField Field { get; set; }

    /// <summary>Width in effective pixels.</summary>
    public double Width { get; set; } = 80;
}

public sealed class ChartWidgetConfig : WidgetConfig
{
    /// <summary>Visible time window.</summary>
    public int WindowSeconds { get; set; } = 300;

    public bool Stacked { get; set; }
    public List<ChartSeriesConfig> Series { get; set; } = [];
}

public sealed class ChartSeriesConfig
{
    public required SeriesRef Source { get; set; }
    public string? Label { get; set; }

    /// <summary>"#RRGGBB"; null = automatic palette.</summary>
    public string? Color { get; set; }
}

/// <summary>Single value with sparkline.</summary>
public sealed class ValueWidgetConfig : WidgetConfig
{
    public required SeriesRef Source { get; set; }
    public int SparklineSeconds { get; set; } = 60;
}
