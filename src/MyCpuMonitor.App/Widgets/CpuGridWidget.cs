using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Monitoring;
using Windows.Foundation;
using Windows.UI;

namespace MyCpuMonitor.Widgets;

/// <summary>
/// One small graph per logical CPU, all drawn on a single canvas. Fraction metrics are shown as % of that CPU
/// (0–100%, independent of the CPU scale toggle: "% of total" makes no sense for one CPU); interrupt rates share
/// one scale so CPUs compare. With Usage, the kernel share is shaded in a second color.
/// </summary>
public sealed partial class CpuGridWidget : UserControl, IMonitorWidget
{
    private const float Gap = 3;

    private static readonly CanvasTextFormat LabelFormat = new() { FontSize = 10, FontFamily = "Segoe UI", WordWrapping = CanvasWordWrapping.NoWrap };
    private static readonly CanvasTextFormat ValueFormat = new()
    {
        FontSize = 10, FontFamily = "Segoe UI", WordWrapping = CanvasWordWrapping.NoWrap, HorizontalAlignment = CanvasHorizontalAlignment.Right,
    };

    private static readonly Color KernelColor = Palette.Series[1];
    private static readonly Color MainFill = Palette.WithAlpha(Palette.Series[0], 0xA0);
    private static readonly Color KernelFill = Palette.WithAlpha(KernelColor, 0xC0);

    private readonly MonitorService _monitor;
    private readonly CpuGridWidgetConfig _config;
    private readonly SeriesData[] _main;
    private readonly SeriesData[] _kernel;
    private readonly SeriesData[] _all;
    private readonly CanvasControl _canvas = new();
    private int _windowSamples = 2;

    public CpuGridWidget(CpuGridWidgetConfig config, MonitorService monitor)
    {
        _config = config;
        _monitor = monitor;
        var cpus = monitor.LogicalProcessors;
        var color = Palette.Series[0];
        _main = Enumerable.Range(0, cpus).Select(c => new SeriesData(new CoreSeries(config.Metric, c), c.ToString(CultureInfo.InvariantCulture), color)).ToArray();
        _kernel = ShowKernel
            ? Enumerable.Range(0, cpus).Select(c => new SeriesData(new CoreSeries(CoreMetric.Kernel, c), "", KernelColor)).ToArray()
            : [];
        _all = [.. _main, .. _kernel];

        _canvas.Draw += OnDraw;
        Content = _canvas;
        Padding = new Thickness(6, 2, 6, 6);
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
        ToolTipService.SetToolTip(this, ShowKernel
            ? $"{MetricCatalog.Get(config.Metric).DisplayName} per logical CPU (kernel share shaded)"
            : $"{MetricCatalog.Get(config.Metric).DisplayName} per logical CPU");
    }

    private bool ShowKernel => _config.ShowKernel && _config.Metric == CoreMetric.Usage;

    public event Action? ConfigChanged
    {
        add { }
        remove { }
    }

    public void Refresh()
    {
        _windowSamples = SeriesData.ReadAll(_monitor, _all, TimeSpan.FromSeconds(_config.WindowSeconds));
        _canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var count = _main.Length;
        double width = sender.ActualWidth, height = sender.ActualHeight;
        var (columns, rows) = GridArrangement.Arrange(count, width, height, _config.Columns);
        if (columns == 0)
            return;

        var cellWidth = (float)((width - Gap * (columns - 1)) / columns);
        var cellHeight = (float)((height - Gap * (rows - 1)) / rows);
        if (cellWidth < 4 || cellHeight < 4)
            return;

        var isFraction = MetricCatalog.Get(_config.Metric).Unit == MetricUnit.Cpu;
        var yMax = isFraction ? 1.0 : SharedMax();
        var theme = ActualTheme;
        var border = Palette.CellBorder(theme);
        var text = Palette.Text(theme);
        var subtle = Palette.SubtleText(theme);
        var showText = cellHeight >= 22 && cellWidth >= 40;

        for (var i = 0; i < count; i++)
        {
            var x = (i % columns) * (cellWidth + Gap);
            var y = (i / columns) * (cellHeight + Gap);
            var cell = new Rect(x, y, cellWidth, cellHeight);
            var main = _main[i];
            ChartRenderer.FillArea(ds, cell, main.Values.AsSpan(0, main.Count), _windowSamples, yMax, MainFill);
            if (_kernel.Length > 0)
            {
                var kernel = _kernel[i];
                ChartRenderer.FillArea(ds, cell, kernel.Values.AsSpan(0, kernel.Count), _windowSamples, yMax, KernelFill);
            }
            // Border last, inset half a pixel so the 1 px line is crisp and drawn over the area's edge.
            ds.DrawRectangle(x + 0.5f, y + 0.5f, cellWidth - 1, cellHeight - 1, border, 1);

            if (showText)
            {
                ds.DrawText(main.Label, x + 3, y + 1, subtle, LabelFormat);
                ds.DrawText(FormatValue(main, isFraction), new Rect(x, y + 1, cellWidth - 3, 14), text, ValueFormat);
            }
        }
    }

    private string FormatValue(SeriesData series, bool isFraction)
    {
        if (series.Error is not null)
            return "error";
        if (series.Count == 0)
            return "";
        return isFraction
            ? (series.Latest * 100).ToString("0", CultureInfo.CurrentCulture) + "%"
            : _monitor.Format(series.Latest, series.Source.Info.Unit);
    }

    /// <summary>Non-fraction metrics (interrupt rate): one nice scale shared by all CPUs.</summary>
    private double SharedMax()
    {
        double max = 0;
        foreach (var s in _main)
        {
            foreach (var v in s.Values.AsSpan(0, s.Count))
                max = Math.Max(max, v);
        }
        return Core.Metrics.AxisScale.NiceCeiling(max, MetricCatalog.Get(_config.Metric).Unit);
    }
}
