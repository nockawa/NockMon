using System.Diagnostics;
using System.Globalization;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NockMon.Core.Folders;
using NockMon.Core.Layout;
using NockMon.Core.Metrics;
using NockMon.Monitoring;
using Windows.Foundation;

namespace NockMon.Widgets;

/// <summary>
/// A folder: its size (large), file count, growth over the chart's window, and size over time (axis fitted to the data,
/// so growth shows on a big folder). The last scan and the largest subfolders are in the tooltip, and listed when the
/// widget is tall enough. Double-click opens the folder.
/// </summary>
public sealed partial class FolderWidget : UserControl, IMonitorWidget
{
    /// <summary>From this height, the largest subfolders are listed under the chart.</summary>
    private const double ListMinHeight = 240;

    private readonly MonitorService _monitor;
    private readonly FolderWidgetConfig _config;
    private readonly SeriesData _size;
    private readonly SeriesData _files;
    private readonly SeriesData[] _all;
    private readonly SeriesData[] _chart;
    private readonly CanvasControl _canvas = new() { IsHitTestVisible = false };
    private readonly TextBlock _sizeText = new() { FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _filesText = new() { FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3) };
    private readonly TextBlock _detail = new() { FontSize = 11, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _largest = new() { FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _tip = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
    private int _windowSamples = 2;

    public FolderWidget(FolderWidgetConfig config, MonitorService monitor)
    {
        _config = config;
        _monitor = monitor;
        _size = new SeriesData(config.Series(FolderMetric.Size), "Size", Palette.SeriesColor(null, 0));
        _files = new SeriesData(config.Series(FolderMetric.Files), "Files", Palette.SeriesColor(null, 1));
        _all = [_size, _files];
        _chart = [_size];

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(_sizeText);
        Grid.SetColumn(_filesText, 1);
        header.Children.Add(_filesText);

        var grid = new Grid { Padding = new Thickness(8, 0, 8, 6), RowSpacing = 2 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(header);
        Grid.SetRow(_detail, 1);
        grid.Children.Add(_detail);
        Grid.SetRow(_canvas, 2);
        _canvas.Margin = new Thickness(0, 4, 0, 0);
        grid.Children.Add(_canvas);
        Grid.SetRow(_largest, 3);
        _largest.Margin = new Thickness(0, 4, 0, 0);
        grid.Children.Add(_largest);
        Content = grid;
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent); // Double-click anywhere.

        ToolTipService.SetToolTip(this, _tip);
        _canvas.Draw += OnDraw;
        DoubleTapped += (_, e) =>
        {
            e.Handled = true; // The board would toggle compact mode too.
            OpenFolder();
        };
        SizeChanged += (_, _) => _largest.Visibility = ActualHeight >= ListMinHeight && _largest.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        var scope = _config.Scope;
        if (string.IsNullOrWhiteSpace(scope.Path))
        {
            Show("–", "", "Choose a folder in the widget's settings.");
            return;
        }

        var window = TimeSpan.FromSeconds(_config.WindowSeconds);
        _windowSamples = SeriesData.ReadAll(_monitor, _all, window);
        var scanner = _monitor.Engine.Folders;
        var last = scanner.Latest(scope);
        var now = DateTime.UtcNow;

        if (_size.Count == 0)
        {
            Show(last?.Error is null ? "…" : "–", "", last?.Error ?? "Scanning…");
            _tip.Text = scope.FullPath + (last?.Error is { } error ? $"\n{error}" : "\nFirst scan in progress.");
            _canvas.Invalidate();
            return;
        }

        var files = _config.ShowFiles ? $"{_monitor.Format(_files.Latest, MetricUnit.Count)} files" : "";
        var detail = last?.Error ?? Growth();
        Show(_monitor.Format(_size.Latest, MetricUnit.Bytes), files, detail);
        _tip.Text = Tooltip(scope, last, scanner.NextScan(scope), now);
        _largest.Text = last is { Largest.Count: > 0 } r ? "Largest: " + string.Join(" · ", r.Largest.Select(p => $"{p.Name} {_monitor.Format(p.Bytes, MetricUnit.Bytes)}")) : "";
        _largest.Visibility = ActualHeight >= ListMinHeight && _largest.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _canvas.Invalidate();
    }

    /// <summary>"+1.2 GB in 1 h" over the samples shown (the window, or since the first scan).</summary>
    private string Growth()
    {
        if (_size.Count < 2)
            return "";
        var delta = _size.Latest - _size.Values[0];
        var span = Duration(_monitor.Engine.Interval * (_size.Count - 1));
        if (delta == 0)
            return $"No change in {span}";
        var sign = delta > 0 ? "+" : "−";
        var files = _files.Count >= 2 && _config.ShowFiles ? _files.Latest - _files.Values[0] : 0; // Same scans as the size.
        var fileText = files == 0 ? "" : $", {(files > 0 ? "+" : "−")}{_monitor.Format(Math.Abs(files), MetricUnit.Count)} files";
        return $"{sign}{_monitor.Format(Math.Abs(delta), MetricUnit.Bytes)}{fileText} in {span}";
    }

    private string Tooltip(FolderScope scope, FolderScanResult? last, DateTime? next, DateTime now)
    {
        var lines = new List<string> { scope.FullPath };
        if (last is not null)
        {
            var scan = $"Scanned {Duration(now - last.TimeUtc)} ago in {Duration(last.Duration, precise: true)}";
            if (next is { } n)
                scan += n <= now ? ", rescanning" : $", next in {Duration(n - now)}";
            lines.Add(scan);
            if (last.Error is not null)
                lines.Add(last.Error);
            lines.Add($"{_monitor.Format(last.Files, MetricUnit.Count)} files, {_monitor.Format(last.Folders, MetricUnit.Count)} folders");
            if (last.Unreadable > 0)
                lines.Add($"{last.Unreadable} folder{(last.Unreadable == 1 ? "" : "s")} could not be read (not counted)");
            if (last.Largest.Count > 0)
                lines.Add("Largest subfolders:\n" + string.Join("\n", last.Largest.Select(p => $"  {p.Name}  {_monitor.Format(p.Bytes, MetricUnit.Bytes)}")));
        }
        var options = new List<string>();
        if (!scope.Recursive)
            options.Add("this folder only");
        if (scope.Include is { Length: > 0 } include)
            options.Add($"files {include}");
        if (scope.Exclude is { Length: > 0 } exclude)
            options.Add($"skipping {exclude}");
        if (scope.SizeOnDisk)
            options.Add("size on disk");
        if (options.Count > 0)
            lines.Add(string.Join(", ", options));
        lines.Add("Double-click to open the folder.");
        return string.Join("\n", lines);
    }

    private void Show(string size, string files, string detail)
    {
        Set(_sizeText, size);
        Set(_filesText, files);
        Set(_detail, detail);
    }

    private static void Set(TextBlock block, string text)
    {
        if (block.Text != text)
            block.Text = text;
    }

    private void OpenFolder()
    {
        var path = _config.Scope.FullPath;
        if (!Directory.Exists(path))
            return;
        try
        {
            // Explorer hands the window to the running (non-elevated) shell.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing to report on the board: the folder simply doesn't open.
        }
    }

    /// <summary>"45 s", "12 min", "1 h 20 min", "3 d 4 h"; with <paramref name="precise"/>, sub-second durations in ms.</summary>
    private static string Duration(TimeSpan span, bool precise = false)
    {
        var c = CultureInfo.CurrentCulture;
        if (precise && span.TotalSeconds < 1)
            return $"{span.TotalMilliseconds.ToString("0", c)} ms";
        if (precise && span.TotalSeconds < 10)
            return $"{span.TotalSeconds.ToString("0.0", c)} s";
        if (span.TotalSeconds < 90)
            return $"{Math.Max(0, (int)Math.Round(span.TotalSeconds))} s";
        if (span.TotalMinutes < 90)
            return $"{(int)Math.Round(span.TotalMinutes)} min";
        if (span.TotalHours < 48)
            return span.Minutes == 0 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalHours} h {span.Minutes} min";
        return $"{(int)span.TotalDays} d {span.Hours} h";
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args) =>
        ChartRenderer.Draw(args.DrawingSession, new Rect(0, 0, sender.ActualWidth, sender.ActualHeight), _chart, _windowSamples,
            new ChartStyle(Stacked: false, ShowLegend: false, ShowGrid: true, ActualTheme, ZeroBased: false), _monitor);
}
