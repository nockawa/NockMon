using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NockMon.Core.Layout;
using NockMon.Monitoring;
using Windows.Foundation;

namespace NockMon.Widgets;

/// <summary>Current value (scaled to the widget) over a sparkline.</summary>
public sealed partial class ValueWidget : UserControl, IMonitorWidget
{
    private readonly MonitorService _monitor;
    private readonly ValueWidgetConfig _config;
    private readonly SeriesData[] _series;
    private readonly CanvasControl _sparkline = new() { IsHitTestVisible = false };
    private readonly TextBlock _value = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private int _windowSamples = 2;

    public ValueWidget(ValueWidgetConfig config, MonitorService monitor)
    {
        _config = config;
        _monitor = monitor;
        _series = [new SeriesData(config.Source, config.Source.DefaultLabel, Palette.SeriesColor(null, 0))];

        var grid = new Grid { Padding = new Thickness(8, 0, 8, 6) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var viewbox = new Viewbox { Child = _value, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(viewbox);
        Grid.SetRow(_sparkline, 1);
        grid.Children.Add(_sparkline);
        Content = grid;

        if (string.IsNullOrEmpty(config.Title))
            ToolTipService.SetToolTip(this, config.Source.DefaultLabel);
        _sparkline.Draw += OnDraw;
        Unloaded += (_, _) => _sparkline.RemoveFromVisualTree();
        ActualThemeChanged += (_, _) => _sparkline.Invalidate();
    }

    public event Action? ConfigChanged
    {
        add { }
        remove { }
    }

    public void Refresh()
    {
        _windowSamples = SeriesData.ReadAll(_monitor, _series, TimeSpan.FromSeconds(_config.SparklineSeconds));
        var s = _series[0];
        _value.Text = s.Error is not null ? "error" : s.Count > 0 ? _monitor.Format(s.Latest, s.Source.Info.Unit) : "–";
        _sparkline.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args) =>
        ChartRenderer.Draw(args.DrawingSession, new Rect(0, 0, sender.ActualWidth, sender.ActualHeight), _series, _windowSamples,
            new ChartStyle(Stacked: false, ShowLegend: false, ShowGrid: false, ActualTheme), _monitor);
}
