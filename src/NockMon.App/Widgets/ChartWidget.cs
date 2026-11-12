using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NockMon.Core.Layout;
using NockMon.Monitoring;
using Windows.Foundation;

namespace NockMon.Widgets;

/// <summary>Series over time; hovering shows the values at the pointed time (<see cref="ChartHover"/>).</summary>
public sealed partial class ChartWidget : UserControl, IMonitorWidget
{
    private readonly MonitorService _monitor;
    private readonly ChartWidgetConfig _config;
    private readonly SeriesData[] _series;
    private readonly SampleTimes _times = new();
    private readonly CanvasControl _canvas = new();
    private readonly ChartHover _hover;
    private int _windowSamples = 2;
    private ChartLayout _layout;

    public ChartWidget(ChartWidgetConfig config, MonitorService monitor)
    {
        _config = config;
        _monitor = monitor;
        _series = config.Series
            .Select((s, i) => new SeriesData(s.Source, string.IsNullOrWhiteSpace(s.Label) ? s.Source.DefaultLabel : s.Label, Palette.SeriesColor(s.Color, i)))
            .ToArray();
        _canvas.Draw += OnDraw;
        var surface = new Grid { Children = { _canvas } };
        Func<SeriesData, float, string> format = (s, v) => monitor.Format(v, s.Source.Unit);
        _hover = new ChartHover(surface, _canvas, (ds, pointer, bounds) =>
            ChartRenderer.DrawHover(ds, _layout, _series, _times, pointer, bounds, CurrentStyle, format));
        surface.Children.Add(_hover.Overlay);
        Content = surface;
        Padding = new Thickness(8, 2, 8, 8);
        Unloaded += (_, _) =>
        {
            _canvas.RemoveFromVisualTree();
            _hover.Overlay.RemoveFromVisualTree();
        };
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
    }

    public event Action? ConfigChanged
    {
        add { }
        remove { }
    }

    public void Refresh()
    {
        _windowSamples = SeriesData.ReadAll(_monitor, _series, TimeSpan.FromSeconds(_config.WindowSeconds), _times);
        _canvas.Invalidate();
    }

    private ChartStyle CurrentStyle => new(_config.Stacked, ShowLegend: true, ShowGrid: true, ActualTheme);

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        _layout = ChartRenderer.Draw(args.DrawingSession, new Rect(0, 0, sender.ActualWidth, sender.ActualHeight), _series, _windowSamples, CurrentStyle, _monitor);
        _hover.ChartDrawn();
    }
}
