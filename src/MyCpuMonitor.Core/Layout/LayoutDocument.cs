using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MyCpuMonitor.Core.Metrics;

namespace MyCpuMonitor.Core.Layout;

/// <summary>
/// A named set of pages (tabs) of widgets. Persisted as JSON. One page: no tab rail. Every page's data is sampled
/// (switching back shows what was missed); only the shown page is drawn.
/// </summary>
public sealed class LayoutDocument
{
    public const int CurrentSchemaVersion = 1;

    private List<LayoutPage> _pages = [new()];
    private int _activePage;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public required string Name { get; set; }
    public BoardConfig Board { get; set; } = new();

    /// <summary>At least one page (an empty list read from a file becomes one empty page).</summary>
    public List<LayoutPage> Pages
    {
        get => _pages;
        set => _pages = value is { Count: > 0 } ? value : [new LayoutPage()];
    }

    /// <summary>Index of the page shown (remembered per layout). Clamped when read: JSON may set it before the pages.</summary>
    public int ActivePage
    {
        get => Math.Clamp(_activePage, 0, _pages.Count - 1);
        set => _activePage = value;
    }

    [JsonIgnore] public LayoutPage CurrentPage => _pages[ActivePage];

    /// <summary>The shown page's widgets: what the board, editing and placement work on.</summary>
    [JsonIgnore]
    public List<WidgetConfig> Widgets
    {
        get => CurrentPage.Widgets;
        set => CurrentPage.Widgets = value;
    }

    /// <summary>Widgets of every page, stacks not expanded.</summary>
    [JsonIgnore] public IEnumerable<WidgetConfig> AllWidgets => _pages.SelectMany(p => p.Widgets);

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

/// <summary>A tab of a layout: its widgets, a name (tooltip) and an icon (Fluent UI System Icons name).</summary>
public sealed class LayoutPage
{
    public const string DefaultIcon = "board";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Main";
    public string Icon { get; set; } = DefaultIcon;
    public List<WidgetConfig> Widgets { get; set; } = [];
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
[JsonDerivedType(typeof(CpuGridWidgetConfig), "cpuGrid")]
[JsonDerivedType(typeof(PoolTagTableWidgetConfig), "poolTags")]
[JsonDerivedType(typeof(ValueListWidgetConfig), "valueList")]
[JsonDerivedType(typeof(DriverTableWidgetConfig), "drivers")]
[JsonDerivedType(typeof(ProcessEventsWidgetConfig), "processEvents")]
[JsonDerivedType(typeof(ObjectTypesWidgetConfig), "objectTypes")]
[JsonDerivedType(typeof(GpuEnginesWidgetConfig), "gpuEngines")]
[JsonDerivedType(typeof(StackWidgetConfig), "stack")]
[JsonDerivedType(typeof(PluginWidgetConfig), "plugin")]
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

    /// <summary>Hide the System Idle Process (PID 0).</summary>
    public bool ExcludeIdle { get; set; }

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

/// <summary>One small graph per logical CPU.</summary>
public sealed class CpuGridWidgetConfig : WidgetConfig
{
    public CoreMetric Metric { get; set; } = CoreMetric.Usage;

    /// <summary>With <see cref="CoreMetric.Usage"/>: shade the kernel part of the usage.</summary>
    public bool ShowKernel { get; set; } = true;

    public int WindowSeconds { get; set; } = 60;

    /// <summary>Grid columns; 0 = automatic (cells kept close to <see cref="GridArrangement.DefaultAspect"/>).</summary>
    public int Columns { get; set; }
}

/// <summary>Kernel pool usage by tag, top N (driver leak hunting).</summary>
public sealed class PoolTagTableWidgetConfig : WidgetConfig
{
    public int TopCount { get; set; } = 20;
    public Sampling.PoolTagField SortBy { get; set; } = Sampling.PoolTagField.NonPagedBytes;
    public bool Descending { get; set; } = true;
}

/// <summary>DPC/ISR activity per driver (ETW): which driver burns CPU in interrupts and which causes latency spikes.</summary>
public sealed class DriverTableWidgetConfig : WidgetConfig
{
    public int TopCount { get; set; } = 15;
    public Sampling.DriverField SortBy { get; set; } = Sampling.DriverField.DpcTime;
    public bool Descending { get; set; } = true;
}

/// <summary>One small graph per engine of a GPU (engines are polled only while shown: ~0.3 ms each per sample).</summary>
public sealed class GpuEnginesWidgetConfig : WidgetConfig
{
    /// <summary>Adapter display name; null = the first adapter.</summary>
    public string? Adapter { get; set; }

    /// <summary>Engine name pattern ("*", "Compute*;3D", "Video*").</summary>
    public string EnginePattern { get; set; } = "*";

    public int WindowSeconds { get; set; } = 60;

    /// <summary>Grid columns; 0 = automatic.</summary>
    public int Columns { get; set; }
}

/// <summary>
/// Several widgets sharing one board area, one shown at a time. The items' own bounds are ignored (the stack's apply).
/// Stacks are never nested.
/// </summary>
public sealed class StackWidgetConfig : WidgetConfig
{
    public List<WidgetConfig> Items { get; set; } = [];

    /// <summary>Seconds between automatic switches to the next item; 0 = manual only.</summary>
    public int CycleSeconds { get; set; }

    /// <summary>Item shown. Runtime state, not persisted (a layout always opens on the first item).</summary>
    [JsonIgnore]
    public int CurrentIndex { get; set; }

    /// <summary>The widgets of <paramref name="widgets"/>, with stacks replaced by their items.</summary>
    public static IEnumerable<WidgetConfig> Leaves(IEnumerable<WidgetConfig> widgets) =>
        widgets.SelectMany(w => w is StackWidgetConfig s ? s.Items : [w]);
}

/// <summary>
/// A widget provided by a plugin (<see cref="Plugin"/> package id, <see cref="Widget"/> id within it). Its settings are
/// the plugin's, kept as JSON: a layout survives the plugin being missing, and works again once it is installed.
/// </summary>
public sealed class PluginWidgetConfig : WidgetConfig
{
    public required string Plugin { get; set; }
    public required string Widget { get; set; }
    public JsonObject? Settings { get; set; }
}

/// <summary>Kernel objects per type: counts, handles, growth. Objects without handles are held by the kernel.</summary>
public sealed class ObjectTypesWidgetConfig : WidgetConfig
{
    public int TopCount { get; set; } = 20;
    public Sampling.ObjectTypeField SortBy { get; set; } = Sampling.ObjectTypeField.Objects;
    public bool Descending { get; set; } = true;
}

/// <summary>Recent process starts and exits (ETW), newest first.</summary>
public sealed class ProcessEventsWidgetConfig : WidgetConfig
{
    public int MaxRows { get; set; } = 30;
    public bool ShowStarts { get; set; } = true;
    public bool ShowExits { get; set; } = true;

    /// <summary>Optional name pattern.</summary>
    public string? Filter { get; set; }
}

/// <summary>Label/value rows for a list of series (e.g. a kernel overview).</summary>
public sealed class ValueListWidgetConfig : WidgetConfig
{
    public List<SeriesRef> Items { get; set; } = [];

    public static List<SeriesRef> KernelOverview() =>
    [
        new SystemSeries(SystemMetric.ContextSwitches),
        new SystemSeries(SystemMetric.SystemCalls),
        new SystemSeries(SystemMetric.Interrupts),
        new SystemSeries(SystemMetric.CpuDpc),
        new SystemSeries(SystemMetric.CpuInterrupt),
        new SystemSeries(SystemMetric.ProcessorQueueLength),
        new SystemSeries(SystemMetric.CpuFrequency),
        new SystemSeries(SystemMetric.TimerResolution),
        new SystemSeries(SystemMetric.PageFaults),
        new SystemSeries(SystemMetric.PageReads),
        new SystemSeries(SystemMetric.MemoryStandby),
        new SystemSeries(SystemMetric.MemoryModified),
        new SystemSeries(SystemMetric.MemoryFree),
        new SystemSeries(SystemMetric.MemoryZeroed),
        new SystemSeries(SystemMetric.KernelPaged),
        new SystemSeries(SystemMetric.KernelNonPaged),
        new SystemSeries(SystemMetric.Handles),
        new SystemSeries(SystemMetric.Threads),
    ];
}

/// <summary>Single value with sparkline.</summary>
public sealed class ValueWidgetConfig : WidgetConfig
{
    public required SeriesRef Source { get; set; }
    public int SparklineSeconds { get; set; } = 60;
}
