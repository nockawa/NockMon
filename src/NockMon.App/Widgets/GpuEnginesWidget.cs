using System.Globalization;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NockMon.Core.Gpu;
using NockMon.Core.Layout;
using NockMon.Core.Metrics;
using NockMon.Core.Model;
using NockMon.Monitoring;
using Windows.UI;

namespace NockMon.Widgets;

/// <summary>
/// One small graph per engine of a GPU (0–100% busy), engines filtered by a name pattern. Engine names arrive shortly
/// after start (until then "Engine N"); the cells are rebuilt when they change.
/// </summary>
public sealed partial class GpuEnginesWidget : UserControl, IMonitorWidget
{
    private static readonly Color Fill = Palette.WithAlpha(Palette.Series[2], 0xB0);

    private readonly MonitorService _monitor;
    private readonly GpuEnginesWidgetConfig _config;
    private readonly CanvasControl _canvas = new();
    private readonly TextBlock _message = new() { Margin = new Thickness(8), Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private SeriesData[] _engines = [];
    private string[]? _engineNames;
    private int _windowSamples = 2;

    public GpuEnginesWidget(GpuEnginesWidgetConfig config, MonitorService monitor)
    {
        _config = config;
        _monitor = monitor;
        _canvas.Draw += (sender, args) => SmallMultiples.Draw(args.DrawingSession, new Windows.Foundation.Size(sender.ActualWidth, sender.ActualHeight), _engines, null, _windowSamples,
            100, _config.Columns, Fill, default, ActualTheme, s => s.Count > 0 ? s.Latest.ToString("0", CultureInfo.CurrentCulture) + "%" : "");
        Content = new Grid { Padding = new Thickness(6, 2, 6, 6), Children = { _canvas, _message } };
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
        var adapter = _monitor.Engine.GpuAdapters.FirstOrDefault(a => _config.Adapter is null || a.Name == _config.Adapter);
        if (adapter is null)
        {
            _message.Text = _config.Adapter is null ? "No GPU found." : $"GPU '{_config.Adapter}' not found.";
            _engines = [];
            _canvas.Invalidate();
            return;
        }
        _message.Text = "";

        var names = adapter.Engines;
        if (!ReferenceEquals(names, _engineNames))
        {
            var pattern = new NamePattern(_config.EnginePattern);
            _engines = names.Where(n => pattern.IsMatch(n))
                .Select(n => new SeriesData(new GpuSeries(GpuMetric.Engine, adapter.Name, n), n, Fill))
                .ToArray();
            _engineNames = names;
            if (_engines.Length == 0)
                _message.Text = $"No engine matches '{_config.EnginePattern}'. Engines: {string.Join(", ", names)}";
        }
        _windowSamples = SeriesData.ReadAll(_monitor, _engines, TimeSpan.FromSeconds(_config.WindowSeconds));
        _canvas.Invalidate();
    }
}
