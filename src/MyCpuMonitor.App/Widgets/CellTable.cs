using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MyCpuMonitor.Widgets;

public sealed record CellColumn(string Header, string Tooltip, double Width, bool RightAligned = true, bool Monospace = false);

/// <summary>
/// Fixed-size text table for tick-rate data: a pool of TextBlocks (rows × columns) built once; per tick only the text
/// of changed cells is assigned (no layout pass otherwise). Clickable headers with a sort indicator, striped rows.
/// </summary>
public sealed partial class CellTable : UserControl
{
    private const double RowHeight = 20;

    private readonly CellColumn[] _columns;
    private readonly TextBlock[] _headers;
    private readonly TextBlock[,] _cells;

    public CellTable(IReadOnlyList<CellColumn> columns, int rows)
    {
        _columns = columns.ToArray();
        rows = Math.Max(1, rows);
        var grid = new Grid { Padding = new Thickness(6, 0, 6, 4), ColumnSpacing = 8 };
        foreach (var column in _columns)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column.Width) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r <= rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight) });

        var stripe = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        for (var r = 2; r <= rows; r += 2)
        {
            var band = new Border { Background = stripe, CornerRadius = new CornerRadius(3), Margin = new Thickness(-4, 0, -4, 0) };
            Grid.SetRow(band, r);
            Grid.SetColumnSpan(band, _columns.Length + 1);
            grid.Children.Add(band);
        }

        var mono = new FontFamily("Cascadia Mono, Consolas");
        _headers = new TextBlock[_columns.Length];
        _cells = new TextBlock[rows, _columns.Length];
        for (var c = 0; c < _columns.Length; c++)
        {
            var column = _columns[c];
            var align = column.RightAligned ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            var header = new TextBlock
            {
                Text = column.Header,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = align,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTipService.SetToolTip(header, column.Tooltip);
            var index = c;
            header.Tapped += (_, _) => HeaderTapped?.Invoke(index);
            Grid.SetColumn(header, c);
            grid.Children.Add(header);
            _headers[c] = header;

            for (var r = 0; r < rows; r++)
            {
                var cell = new TextBlock
                {
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = align,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false,
                };
                if (column.Monospace)
                    cell.FontFamily = mono;
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
        SetSortIndicator(-1, true);
    }

    /// <summary>Header click (column index).</summary>
    public event Action<int>? HeaderTapped;

    public int RowCount => _cells.GetLength(0);
    public int ColumnCount => _columns.Length;

    /// <summary>Sets a cell's text; no-op (no layout invalidation) when unchanged.</summary>
    public void SetCell(int row, int column, string text)
    {
        var cell = _cells[row, column];
        if (cell.Text != text)
            cell.Text = text;
    }

    /// <summary>Clears the rows from <paramref name="firstRow"/> on.</summary>
    public void ClearFrom(int firstRow)
    {
        for (var r = firstRow; r < RowCount; r++)
        {
            for (var c = 0; c < ColumnCount; c++)
                SetCell(r, c, "");
        }
    }

    /// <summary>Marks the sorted column (▼/▲, accent color); -1 for none.</summary>
    public void SetSortIndicator(int column, bool descending)
    {
        var accent = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        for (var c = 0; c < _headers.Length; c++)
        {
            _headers[c].Text = c == column ? (descending ? "▼ " : "▲ ") + _columns[c].Header : _columns[c].Header;
            _headers[c].Foreground = c == column ? accent : secondary;
        }
    }
}
