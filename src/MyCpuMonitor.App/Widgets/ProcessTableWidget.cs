using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Sampling;
using MyCpuMonitor.Monitoring;

namespace MyCpuMonitor.Widgets;

/// <summary>
/// Top-N process table. A fixed pool of TextBlocks (TopCount × columns) is built once per configuration
/// and only their text changes per tick; text is assigned only when it differs, to avoid layout passes.
/// Clicking a header sorts by that column (again: reverses the order).
/// </summary>
public sealed partial class ProcessTableWidget : UserControl, IMonitorWidget
{
    private const double RowHeight = 20;

    private readonly MonitorService _monitor;
    private readonly ProcessTableWidgetConfig _config;
    private readonly ProcessQuery _query = new();
    private readonly TextBlock[] _headers;
    private readonly TextBlock[,] _cells;
    private long _lastScanIndex = -1;
    private int _lastDisplayVersion = -1;
    private bool _queryDirty = true;

    public ProcessTableWidget(ProcessTableWidgetConfig config, MonitorService monitor)
    {
        _config = config;
        _monitor = monitor;

        var columns = config.Columns;
        var rows = Math.Max(1, config.TopCount);
        var grid = new Grid { Padding = new Thickness(6, 0, 6, 4), ColumnSpacing = 8 };
        foreach (var column in columns)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column.Width) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r <= rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight) });

        var stripe = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        for (var r = 2; r <= rows; r += 2)
        {
            var band = new Border { Background = stripe, CornerRadius = new CornerRadius(3), Margin = new Thickness(-4, 0, -4, 0) };
            Grid.SetRow(band, r);
            Grid.SetColumnSpan(band, columns.Count + 1);
            grid.Children.Add(band);
        }

        _headers = new TextBlock[columns.Count];
        for (var c = 0; c < columns.Count; c++)
        {
            var field = columns[c].Field;
            var header = new TextBlock
            {
                Text = MetricCatalog.Get(field).ShortName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = IsRightAligned(field) ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTipService.SetToolTip(header, MetricCatalog.Get(field).DisplayName);
            header.Tapped += (_, _) => SortBy(field);
            Grid.SetColumn(header, c);
            grid.Children.Add(header);
            _headers[c] = header;
        }

        _cells = new TextBlock[rows, columns.Count];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                var cell = new TextBlock
                {
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = IsRightAligned(columns[c].Field) ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false,
                };
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
                _cells[r, c] = cell;
            }
        }

        Content = new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        UpdateHeaders();
    }

    public event Action? ConfigChanged;

    public void Refresh()
    {
        // The table only changes with a new scan (slower than samples), a sort change or a display setting change.
        var options = new ProcessQueryOptions(_config.SortBy, _config.Descending, _config.TopCount, _config.GroupByName, _config.Filter);
        var ran = _monitor.Engine.Read((widget: this, options), static (state, snapshot, _) =>
        {
            var w = state.widget;
            if (snapshot.Table.ScanIndex == w._lastScanIndex && !w._queryDirty)
                return false;
            w._lastScanIndex = snapshot.Table.ScanIndex;
            w._query.Run(snapshot, state.options);
            return true;
        });
        if (!ran && _monitor.DisplayVersion == _lastDisplayVersion)
            return;
        _queryDirty = false;
        _lastDisplayVersion = _monitor.DisplayVersion;

        // Results are owned by the query: read them outside the engine lock.
        var rows = _query.Results;
        var columns = _config.Columns;
        for (var r = 0; r < _cells.GetLength(0); r++)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                var text = r < rows.Length ? FormatCell(in rows[r], columns[c].Field) : "";
                var cell = _cells[r, c];
                if (cell.Text != text)
                    cell.Text = text;
            }
        }
    }

    private string FormatCell(in ProcessRow row, ProcessField field)
    {
        var grouped = _config.GroupByName && row.Instances > 1;
        return field switch
        {
            ProcessField.Name => row.Stats.Name,
            ProcessField.Pid or ProcessField.ParentPid or ProcessField.SessionId when grouped => "",
            ProcessField.Pid or ProcessField.ParentPid or ProcessField.SessionId or ProcessField.BasePriority =>
                ((long)row.GetValue(field)).ToString(CultureInfo.InvariantCulture),
            ProcessField.StartTime => FormatStart(row.Stats.CreateTime),
            _ => _monitor.Format(row.GetValue(field), MetricCatalog.Get(field).Unit),
        };
    }

    private static string FormatStart(long fileTime)
    {
        if (fileTime <= 0)
            return "";
        var local = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("T", CultureInfo.CurrentCulture) : local.ToString("g", CultureInfo.CurrentCulture);
    }

    private static bool IsRightAligned(ProcessField field) => field is not (ProcessField.Name or ProcessField.StartTime);

    private void SortBy(ProcessField field)
    {
        if (_config.SortBy == field)
            _config.Descending = !_config.Descending;
        else
        {
            _config.SortBy = field;
            _config.Descending = field != ProcessField.Name;
        }
        UpdateHeaders();
        _queryDirty = true;
        Refresh();
        ConfigChanged?.Invoke();
    }

    private void UpdateHeaders()
    {
        for (var c = 0; c < _headers.Length; c++)
        {
            var field = _config.Columns[c].Field;
            var name = MetricCatalog.Get(field).ShortName;
            _headers[c].Text = field == _config.SortBy ? (_config.Descending ? "▼ " : "▲ ") + name : name;
            _headers[c].Foreground = field == _config.SortBy
                ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
    }
}
