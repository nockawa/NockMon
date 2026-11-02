using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NockMon.Widgets;

/// <param name="Icon">Left-aligned columns only: a 16×16 icon before the text of each cell (<see cref="CellTable.SetIcon"/>).</param>
/// <param name="Doc">When set, the header tooltip shows its title and summary instead of <paramref name="Tooltip"/>.</param>
public sealed record CellColumn(string Header, string Tooltip, double Width, bool RightAligned = true, bool Monospace = false, bool Icon = false,
    Core.Help.Doc? Doc = null);

/// <summary>
/// Fixed-size text table for tick-rate data: a pool of TextBlocks (rows × columns) built once; per tick only the text
/// of changed cells is assigned (no layout pass otherwise). Clickable headers with a sort indicator, striped rows.
/// </summary>
public sealed partial class CellTable : UserControl
{
    private const double RowHeight = 20;
    private const double IconSize = 16;

    private readonly CellColumn[] _columns;
    private readonly TextBlock[] _headers;
    private readonly TextBlock[,] _cells;
    private readonly Image?[,] _icons;
    private readonly Grid _grid;
    private readonly Border _highlight;

    public CellTable(IReadOnlyList<CellColumn> columns, int rows)
    {
        _columns = columns.ToArray();
        rows = Math.Max(1, rows);
        // A background: right-clicks between the cells' text hit the grid (cells are not hit-test visible).
        var grid = _grid = new Grid { Padding = new Thickness(6, 0, 6, 4), ColumnSpacing = 8, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
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

        _highlight = new Border
        {
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Opacity = 0.3,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(-4, 0, -4, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        Grid.SetColumnSpan(_highlight, _columns.Length + 1);
        grid.Children.Add(_highlight);
        grid.RightTapped += OnGridRightTapped;

        var mono = new FontFamily("Cascadia Mono, Consolas");
        _headers = new TextBlock[_columns.Length];
        _cells = new TextBlock[rows, _columns.Length];
        _icons = new Image?[rows, _columns.Length];
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
            ToolTipService.SetToolTip(header, column.Doc is { } doc ? Help.DocView.Tooltip(doc) : column.Tooltip);
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
                if (column.Icon)
                {
                    var icon = new Image
                    {
                        Width = IconSize,
                        Height = IconSize,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = false,
                    };
                    Grid.SetRow(icon, r + 1);
                    Grid.SetColumn(icon, c);
                    grid.Children.Add(icon);
                    _icons[r, c] = icon;
                    cell.Margin = new Thickness(IconSize + 4, 0, 0, 0);
                }
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

    /// <summary>Right-click (or press and hold) on a row: row index, the element and position to show a menu at.</summary>
    public event Action<int, FrameworkElement, Windows.Foundation.Point>? RowRightTapped;

    public int RowCount => _cells.GetLength(0);
    public int ColumnCount => _columns.Length;

    /// <summary>Sets a cell's text; no-op (no layout invalidation) when unchanged.</summary>
    public void SetCell(int row, int column, string text)
    {
        var cell = _cells[row, column];
        if (cell.Text != text)
            cell.Text = text;
    }

    public string GetCell(int row, int column) => _cells[row, column].Text;

    public string GetHeader(int column) => _columns[column].Header;

    /// <summary>Sets a cell's icon (icon columns only); no-op when unchanged.</summary>
    public void SetIcon(int row, int column, ImageSource? source)
    {
        if (_icons[row, column] is { } icon && !ReferenceEquals(icon.Source, source))
            icon.Source = source;
    }

    /// <summary>Clears the rows from <paramref name="firstRow"/> on.</summary>
    public void ClearFrom(int firstRow)
    {
        for (var r = firstRow; r < RowCount; r++)
        {
            for (var c = 0; c < ColumnCount; c++)
            {
                SetCell(r, c, "");
                SetIcon(r, c, null);
            }
        }
    }

    /// <summary>Highlights a row (the one a menu is open for); null for none.</summary>
    public void HighlightRow(int? row)
    {
        _highlight.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is { } r)
            Grid.SetRow(_highlight, r + 1);
    }

    private void OnGridRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        var position = e.GetPosition(_grid);
        var row = (int)(position.Y / RowHeight) - 1; // Row 0 is the header.
        if (row < 0 || row >= RowCount)
            return;
        e.Handled = true;
        RowRightTapped?.Invoke(row, _grid, position);
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
