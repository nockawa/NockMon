using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Monitoring;
using Windows.Foundation;

namespace MyCpuMonitor.Widgets;

public sealed partial class ChartWidget : UserControl, IMonitorWidget
{
    private readonly MonitorService _monitor;
    private readonly ChartWidgetConfig _config;
    private readonly SeriesData[] _series;
    private readonly CanvasControl _canvas = new();
    private int _windowSamples = 2;

    public ChartWidget(ChartWidgetConfig config, MonitorService monitor)
    {
        _config = config;
        _monitor = monitor;
        _series = config.Series
            .Select((s, i) => new SeriesData(s.Source, string.IsNullOrWhiteSpace(s.Label) ? s.Source.DefaultLabel : s.Label, Palette.SeriesColor(s.Color, i)))
            .ToArray();
        _canvas.Draw += OnDraw;
        Content = _canvas;
        Padding = new Thickness(8, 2, 8, 8);
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
    }

    public event Action? ConfigChanged
    {
        add { }
        remove { }
    }

    public void Refresh()
    {
        _windowSamples = SeriesData.ReadAll(_monitor, _series, TimeSpan.FromSeconds(_config.WindowSeconds));
        _canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args) =>
        ChartRenderer.Draw(args.DrawingSession, new Rect(0, 0, sender.ActualWidth, sender.ActualHeight), _series, _windowSamples,
            new ChartStyle(_config.Stacked, ShowLegend: true, ShowGrid: true, ActualTheme), _monitor);
}
