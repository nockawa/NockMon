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
        var isFraction = MetricCatalog.Get(_config.Metric).Unit == MetricUnit.Cpu;
        SmallMultiples.Draw(args.DrawingSession, new Windows.Foundation.Size(sender.ActualWidth, sender.ActualHeight), _main, _kernel.Length > 0 ? _kernel : null, _windowSamples,
            isFraction ? 1.0 : SharedMax(), _config.Columns, MainFill, KernelFill, ActualTheme, s => FormatValue(s, isFraction));
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
