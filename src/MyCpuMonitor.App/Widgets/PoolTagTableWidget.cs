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
/// Top-N kernel pool tags (scan rate), same cell-pool approach as the process table. Header click sorts.
/// Sustained positive growth of a tag is the signature of a driver leak.
/// </summary>
public sealed partial class PoolTagTableWidget : UserControl, IMonitorWidget
{
    private const double RowHeight = 20;

    private static readonly (PoolTagField Field, string Header, string Tooltip, double Width)[] Columns =
    [
        (PoolTagField.Tag, "Tag", "Pool tag (4 characters, owner driver/component)", 52),
        (PoolTagField.NonPagedBytes, "Non-paged", "Non-paged pool bytes", 80),
        (PoolTagField.PagedBytes, "Paged", "Paged pool bytes", 80),
        (PoolTagField.NonPagedGrowth, "NP growth", "Non-paged bytes per second since the previous scan", 84),
        (PoolTagField.PagedGrowth, "P growth", "Paged bytes per second since the previous scan", 84),
        (PoolTagField.AllocationRate, "Allocs", "Allocations per second", 72),
        (PoolTagField.Outstanding, "Live", "Allocations not freed", 72),
    ];

    private readonly MonitorService _monitor;
    private readonly PoolTagTableWidgetConfig _config;
    private readonly PoolTagQuery _query = new();
    private readonly TextBlock[] _headers;
    private readonly TextBlock[,] _cells;
    private long _lastScanIndex = -1;
    private bool _dirty = true;

    public PoolTagTableWidget(PoolTagTableWidgetConfig config, MonitorService monitor)
    {
        _config = config;
        _monitor = monitor;
        var rows = Math.Max(1, config.TopCount);
        var grid = new Grid { Padding = new Thickness(6, 0, 6, 4), ColumnSpacing = 8 };
        foreach (var column in Columns)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column.Width) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r <= rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight) });

        var stripe = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        for (var r = 2; r <= rows; r += 2)
        {
            var band = new Border { Background = stripe, CornerRadius = new CornerRadius(3), Margin = new Thickness(-4, 0, -4, 0) };
            Grid.SetRow(band, r);
            Grid.SetColumnSpan(band, Columns.Length + 1);
            grid.Children.Add(band);
        }

        _headers = new TextBlock[Columns.Length];
        _cells = new TextBlock[rows, Columns.Length];
        for (var c = 0; c < Columns.Length; c++)
        {
            var (field, header, tooltip, _) = Columns[c];
            var align = field == PoolTagField.Tag ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            var h = new TextBlock { Text = header, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = align };
            ToolTipService.SetToolTip(h, tooltip);
            h.Tapped += (_, _) => SortBy(field);
            Grid.SetColumn(h, c);
            grid.Children.Add(h);
            _headers[c] = h;

            for (var r = 0; r < rows; r++)
            {
                var cell = new TextBlock
                {
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = align,
                    IsHitTestVisible = false,
                    FontFamily = field == PoolTagField.Tag ? new FontFamily("Cascadia Mono, Consolas") : FontFamily,
                };
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
                _cells[r, c] = cell;
            }
        }

        Content = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto };
        UpdateHeaders();
    }

    public event Action? ConfigChanged;

    public void Refresh()
    {
        var ran = _monitor.Engine.Read(this, static (w, snapshot, _) =>
        {
            if (snapshot.Table.ScanIndex == w._lastScanIndex && !w._dirty)
                return false;
            w._lastScanIndex = snapshot.Table.ScanIndex;
            w._query.Run(snapshot.Table, w._config.SortBy, w._config.Descending, w._config.TopCount);
            return true;
        });
        if (!ran)
            return;
        _dirty = false;

        var rows = _query.Results;
        for (var r = 0; r < _cells.GetLength(0); r++)
        {
            for (var c = 0; c < Columns.Length; c++)
            {
                var text = r < rows.Length ? Format(in rows[r], Columns[c].Field) : "";
                if (_cells[r, c].Text != text)
                    _cells[r, c].Text = text;
            }
        }
    }

    private static string Format(in PoolTagStats tag, PoolTagField field) => field switch
    {
        PoolTagField.Tag => tag.Tag,
        PoolTagField.NonPagedBytes or PoolTagField.PagedBytes or PoolTagField.TotalBytes => MetricFormatter.FormatBytes(tag.GetValue(field)),
        PoolTagField.NonPagedGrowth or PoolTagField.PagedGrowth => FormatGrowth(tag.GetValue(field)),
        PoolTagField.AllocationRate => tag.AllocationRate.ToString("#,0", CultureInfo.CurrentCulture) + "/s",
        PoolTagField.Outstanding => tag.Outstanding.ToString("#,0", CultureInfo.CurrentCulture),
        _ => "",
    };

    private static string FormatGrowth(double bytesPerSecond) => bytesPerSecond == 0
        ? "0"
        : (bytesPerSecond > 0 ? "+" : "") + MetricFormatter.FormatBytes(bytesPerSecond) + "/s";

    private void SortBy(PoolTagField field)
    {
        if (_config.SortBy == field)
            _config.Descending = !_config.Descending;
        else
        {
            _config.SortBy = field;
            _config.Descending = field != PoolTagField.Tag;
        }
        UpdateHeaders();
        _dirty = true;
        Refresh();
        ConfigChanged?.Invoke();
    }

    private void UpdateHeaders()
    {
        for (var c = 0; c < Columns.Length; c++)
        {
            var (field, header, _, _) = Columns[c];
            var sorted = field == _config.SortBy;
            _headers[c].Text = sorted ? (_config.Descending ? "▼ " : "▲ ") + header : header;
            _headers[c].Foreground = (Brush)Application.Current.Resources[sorted ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
        }
    }
}
