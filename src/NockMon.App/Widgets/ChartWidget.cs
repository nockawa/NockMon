using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NockMon.Core.Layout;
using NockMon.Monitoring;
using Windows.Foundation;

namespace NockMon.Widgets;

/// <summary>Series over time; hovering shows the values at the pointed time (an overlay: the chart isn't redrawn).</summary>
public sealed partial class ChartWidget : UserControl, IMonitorWidget
{
    private readonly MonitorService _monitor;
    private readonly ChartWidgetConfig _config;
    private readonly SeriesData[] _series;
    private readonly SampleTimes _times = new();
    private readonly CanvasControl _canvas = new();
    private readonly CanvasControl _hover = new() { IsHitTestVisible = false };
    private int _windowSamples = 2;
    private ChartLayout _layout;
    private Point? _pointer;

    public ChartWidget(ChartWidgetConfig config, MonitorService monitor)
    {
        _config = config;
        _monitor = monitor;
        _series = config.Series
            .Select((s, i) => new SeriesData(s.Source, string.IsNullOrWhiteSpace(s.Label) ? s.Source.DefaultLabel : s.Label, Palette.SeriesColor(s.Color, i)))
            .ToArray();
        _canvas.Draw += OnDraw;
        _hover.Draw += OnDrawHover;
        // A background: the whole area takes the pointer, not only the drawn pixels.
        var surface = new Grid { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { _canvas, _hover } };
        Content = surface;
        Padding = new Thickness(8, 2, 8, 8);
        surface.PointerMoved += (_, e) =>
        {
            _pointer = e.GetCurrentPoint(_canvas).Position;
            _hover.Invalidate();
        };
        surface.PointerExited += (_, _) => ClearHover();
        surface.PointerCanceled += (_, _) => ClearHover();
        Unloaded += (_, _) =>
        {
            _canvas.RemoveFromVisualTree();
            _hover.RemoveFromVisualTree();
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

    private void ClearHover()
    {
        _pointer = null;
        _hover.Invalidate();
    }

    private ChartStyle CurrentStyle => new(_config.Stacked, ShowLegend: true, ShowGrid: true, ActualTheme);

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        _layout = ChartRenderer.Draw(args.DrawingSession, new Rect(0, 0, sender.ActualWidth, sender.ActualHeight), _series, _windowSamples, CurrentStyle, _monitor);
        if (_pointer is not null) // New data or layout under the pointer: the readout follows.
            _hover.Invalidate();
    }

    private void OnDrawHover(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_pointer is { } pointer)
            ChartRenderer.DrawHover(args.DrawingSession, _layout, _series, _times, pointer, CurrentStyle, _monitor);
    }
}
