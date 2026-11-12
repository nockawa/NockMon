using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NockMon.Core.Layout;
using NockMon.Core.Metrics;
using NockMon.Monitoring;
using Windows.Foundation;
using Windows.UI;

namespace NockMon.Widgets;

/// <summary>
/// One small graph per logical CPU, all drawn on a single canvas. Fraction metrics are shown as % of that CPU
/// (0–100%, independent of the CPU scale toggle: "% of total" makes no sense for one CPU); interrupt rates share
/// one scale so CPUs compare. With Usage, the kernel share is shaded in a second color. Hovering a cell shows its values
/// at the pointed time.
/// </summary>
public sealed partial class CpuGridWidget : UserControl, IMonitorWidget
{
    private static readonly Color KernelColor = Palette.Series[1];
    // Light fill, top edge outlined in the stronger tone.
    private static readonly Color MainFill = Palette.WithAlpha(Palette.Series[0], 0x40);
    private static readonly Color MainStroke = Palette.WithAlpha(Palette.Series[0], 0xA0);
    private static readonly Color KernelFill = Palette.WithAlpha(KernelColor, 0x50);
    private static readonly Color KernelStroke = Palette.WithAlpha(KernelColor, 0xC0);

    private readonly MonitorService _monitor;
    private readonly CpuGridWidgetConfig _config;
    private readonly SeriesData[] _main;
    private readonly SeriesData[] _kernel;
    private readonly SeriesData[] _all;
    private readonly SampleTimes _times = new();
    private readonly CanvasControl _canvas = new();
    private readonly ChartHover _hover;
    private int _windowSamples = 2;

    public CpuGridWidget(CpuGridWidgetConfig config, MonitorService monitor)
    {
        _config = config;
        _monitor = monitor;
        var cpus = monitor.LogicalProcessors;
        var color = Palette.Series[0];
        _main = Enumerable.Range(0, cpus).Select(c => new SeriesData(new CoreSeries(config.Metric, c), c.ToString(CultureInfo.InvariantCulture), color)).ToArray();
        _kernel = ShowKernel
            ? Enumerable.Range(0, cpus).Select(c => new SeriesData(new CoreSeries(CoreMetric.Kernel, c), "Kernel", KernelColor)).ToArray()
            : [];
        _all = [.. _main, .. _kernel];

        _canvas.Draw += OnDraw;
        var surface = new Grid { Children = { _canvas } };
        // The readout names the metric and the kernel share: no widget tooltip, it would cover it.
        var metric = MetricCatalog.Get(config.Metric);
        var isFraction = metric.Unit == MetricUnit.Cpu;
        Func<SeriesData, float, string> format = (s, v) => FormatValue(v, s.Source.Unit, isFraction);
        Func<SeriesData, string> title = s => $"CPU {s.Label}";
        Func<SeriesData, string> label = s => s.Source is CoreSeries { Metric: CoreMetric.Kernel } ? s.Label : metric.DisplayName;
        _hover = new ChartHover(surface, _canvas, (ds, pointer, bounds) =>
            SmallMultiples.DrawHover(ds, new Windows.Foundation.Size(_canvas.ActualWidth, _canvas.ActualHeight), _main, _kernel.Length > 0 ? _kernel : null,
                _times, _windowSamples, isFraction ? 1.0 : _sharedMax, _config.Columns, pointer, bounds, ActualTheme, format, title, label));
        surface.Children.Add(_hover.Overlay);
        Content = surface;
        Padding = new Thickness(6, 2, 6, 6);
        Unloaded += (_, _) =>
        {
            _canvas.RemoveFromVisualTree();
            _hover.Overlay.RemoveFromVisualTree();
        };
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
    }

    private bool ShowKernel => _config.ShowKernel && _config.Metric == CoreMetric.Usage;

    public event Action? ConfigChanged
    {
        add { }
        remove { }
    }

    public void Refresh()
    {
        _windowSamples = SeriesData.ReadAll(_monitor, _all, TimeSpan.FromSeconds(_config.WindowSeconds), _times);
        _canvas.Invalidate();
    }

    // Created once: a capturing lambda per frame otherwise.
    private Func<SeriesData, string>? _formatFractionCache, _formatValueCache;
    private Func<SeriesData, string> FormatFraction => _formatFractionCache ??= s => FormatValue(s, true);
    private Func<SeriesData, string> FormatPlain => _formatValueCache ??= s => FormatValue(s, false);

    /// <summary>Scale of the last frame, for the hover readout.</summary>
    private double _sharedMax = 1;

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var isFraction = MetricCatalog.Get(_config.Metric).Unit == MetricUnit.Cpu;
        if (!isFraction)
            _sharedMax = SharedMax();
        SmallMultiples.Draw(args.DrawingSession, new Windows.Foundation.Size(sender.ActualWidth, sender.ActualHeight), _main, _kernel.Length > 0 ? _kernel : null, _windowSamples,
            isFraction ? 1.0 : _sharedMax, _config.Columns, MainFill, KernelFill, ActualTheme, isFraction ? FormatFraction : FormatPlain, MainStroke, KernelStroke, boldText: true);
        _hover.ChartDrawn();
    }

    private string FormatValue(SeriesData series, bool isFraction)
    {
        if (series.Error is not null)
            return "error";
        return series.Count == 0 ? "" : FormatValue(series.Latest, series.Source.Unit, isFraction);
    }

    private string FormatValue(float value, MetricUnit unit, bool isFraction) =>
        isFraction ? (value * 100).ToString("0", CultureInfo.CurrentCulture) + "%" : _monitor.Format(value, unit);

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
