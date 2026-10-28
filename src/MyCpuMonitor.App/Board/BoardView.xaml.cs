using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyCpuMonitor.Core.Layout;

namespace MyCpuMonitor.Board;

/// <summary>
/// The widget surface. Positions widgets from their cell bounds; in edit mode shows the cell grid and
/// handles move/resize with a snapped preview (green: valid, red: collision or out of the visible board).
/// The window size only changes how many cells are visible: widgets keep their cells.
/// </summary>
public sealed partial class BoardView : UserControl
{
    private const int MaxGridCells = 20_000;

    private static readonly Brush ValidFill = new SolidColorBrush(ColorHelper.FromArgb(0x30, 0x00, 0xC0, 0x60));
    private static readonly Brush ValidStroke = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x00, 0xC0, 0x60));
    private static readonly Brush InvalidFill = new SolidColorBrush(ColorHelper.FromArgb(0x30, 0xE0, 0x30, 0x30));
    private static readonly Brush InvalidStroke = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xE0, 0x30, 0x30));

    private readonly List<WidgetHost> _hosts = [];
    private LayoutDocument? _layout;
    private BoardGeometry _geometry = new(64, 4);
    private bool _isEditing;
    private DragState? _drag;

    private sealed class DragState(WidgetHost host, DragKind kind)
    {
        public readonly WidgetHost Host = host;
        public readonly DragKind Kind = kind;
        public readonly GridRect Origin = host.Config.Bounds;
        public GridRect Candidate = host.Config.Bounds;
        public bool IsValid = true;
    }

    public BoardView()
    {
        InitializeComponent();
    }

    /// <summary>Creates the content of a widget. Set before assigning <see cref="Layout"/>.</summary>
    internal Func<WidgetConfig, UIElement>? ContentFactory { get; set; }

    /// <summary>Raised when the user moved, resized or removed a widget.</summary>
    public event EventHandler? LayoutEdited;

    /// <summary>Raised when a widget changed its own configuration outside the edit dialog (e.g. table sort).</summary>
    public event EventHandler? WidgetConfigChanged;

    /// <summary>Raised when the user asks to configure a widget.</summary>
    internal event Action<WidgetConfig>? ConfigureRequested;

    /// <summary>Pulls fresh data into every widget.</summary>
    public void RefreshWidgets()
    {
        foreach (var host in _hosts)
            host.Widget?.Refresh();
    }

    /// <summary>Replaces a widget's configuration (same position in the layout) and recreates its content.</summary>
    internal void ReplaceWidget(WidgetConfig old, WidgetConfig replacement)
    {
        if (_layout is null)
            return;
        var index = _layout.Widgets.IndexOf(old);
        if (index < 0)
            return;
        _layout.Widgets[index] = replacement;
        Rebuild();
        RefreshWidgets();
        LayoutEdited?.Invoke(this, EventArgs.Empty);
    }

    public int VisibleColumns { get; private set; }
    public int VisibleRows { get; private set; }

    /// <summary>Internal: a public property would make the XAML compiler generate activation code for LayoutDocument.</summary>
    internal LayoutDocument? Layout
    {
        get => _layout;
        set
        {
            _layout = value;
            Rebuild();
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            _isEditing = value;
            foreach (var host in _hosts)
                host.IsEditing = value;
            UpdateGridLines();
        }
    }

    /// <summary>Recreates all widget hosts from the layout (after widgets were added or the layout replaced).</summary>
    public void Rebuild()
    {
        foreach (var host in _hosts)
            Surface.Children.Remove(host);
        _hosts.Clear();
        _drag = null;
        Preview.Visibility = Visibility.Collapsed;

        if (_layout is null)
            return;

        _geometry = new BoardGeometry(_layout.Board);
        var scroll = _layout.Board.Overflow == BoardOverflow.Scroll;
        Scroller.HorizontalScrollMode = Scroller.VerticalScrollMode = scroll ? ScrollMode.Auto : ScrollMode.Disabled;
        Scroller.HorizontalScrollBarVisibility = Scroller.VerticalScrollBarVisibility = scroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;

        foreach (var widget in _layout.Widgets)
            AddHost(widget);
        UpdateSurface();
        RefreshWidgets();
    }

    /// <summary>Bounds of the area the user can place widgets in (visible cells, or unbounded when the board scrolls).</summary>
    public GridSize PlacementBounds => _layout?.Board.Overflow == BoardOverflow.Scroll
        ? new GridSize(int.MaxValue, int.MaxValue)
        : new GridSize(VisibleColumns, VisibleRows);

    private void AddHost(WidgetConfig widget)
    {
        var content = ContentFactory?.Invoke(widget) ?? new TextBlock { Text = widget.Title };
        var host = new WidgetHost(widget, content, Surface) { IsEditing = _isEditing };
        if (host.Widget is not null)
            host.Widget.ConfigChanged += () => WidgetConfigChanged?.Invoke(this, EventArgs.Empty);
        host.DragStarted += OnDragStarted;
        host.DragDelta += OnDragDelta;
        host.DragCompleted += OnDragCompleted;
        host.DeleteRequested += OnDeleteRequested;
        host.ConfigureRequested += h => ConfigureRequested?.Invoke(h.Config);
        _hosts.Add(host);
        Surface.Children.Insert(1, host); // Above the grid lines, below the preview.
        Place(host, widget.Bounds);
    }

    private void Place(WidgetHost host, GridRect bounds)
    {
        var px = _geometry.ToPixels(bounds);
        Canvas.SetLeft(host, px.X);
        Canvas.SetTop(host, px.Y);
        host.Width = px.Width;
        host.Height = px.Height;
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSurface();

    /// <summary>Recomputes visible cells and the surface size (viewport, or widget extent when scrolling).</summary>
    private void UpdateSurface()
    {
        double viewportWidth = Scroller.ActualWidth, viewportHeight = Scroller.ActualHeight;
        VisibleColumns = _geometry.FitCount(viewportWidth);
        VisibleRows = _geometry.FitCount(viewportHeight);

        var width = viewportWidth;
        var height = viewportHeight;
        if (_layout?.Board.Overflow == BoardOverflow.Scroll)
        {
            var extent = BoardRules.Extent(_layout.Widgets);
            // One spare row/column so widgets can be dragged beyond the current extent.
            width = Math.Max(width, _geometry.Extent(extent.Columns + 1));
            height = Math.Max(height, _geometry.Extent(extent.Rows + 1));
        }
        Surface.Width = width;
        Surface.Height = height;
        UpdateGridLines();
    }

    private void UpdateGridLines()
    {
        if (!_isEditing || _layout is null)
        {
            GridLines.Visibility = Visibility.Collapsed;
            return;
        }

        var columns = _geometry.FitCount(Surface.Width);
        var rows = _geometry.FitCount(Surface.Height);
        var group = new GeometryGroup();
        if ((long)columns * rows <= MaxGridCells)
        {
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < columns; c++)
                {
                    var px = _geometry.ToPixels(new GridRect(c, r, 1, 1));
                    group.Children.Add(new RectangleGeometry { Rect = new(px.X, px.Y, px.Width, px.Height) });
                }
            }
        }
        GridLines.Data = group;
        GridLines.Visibility = Visibility.Visible;
    }

    private void OnDragStarted(WidgetHost host, DragKind kind)
    {
        _drag = new DragState(host, kind);
        Canvas.SetZIndex(host, 50);
        ShowPreview(_drag.Origin, true);
    }

    private void OnDragDelta(WidgetHost host, double dx, double dy)
    {
        if (_drag?.Host != host || _layout is null)
            return;

        var origin = _geometry.ToPixels(_drag.Origin);
        if (_drag.Kind == DragKind.Move)
        {
            _drag.Candidate = _geometry.Move(_drag.Origin, dx, dy);
            Canvas.SetLeft(host, origin.X + dx);
            Canvas.SetTop(host, origin.Y + dy);
        }
        else
        {
            var min = WidgetCatalog.MinSize(host.Config);
            _drag.Candidate = _geometry.Resize(_drag.Origin, dx, dy, min);
            var minPx = _geometry.ToPixels(new GridRect(0, 0, min.Columns, min.Rows));
            host.Width = Math.Max(minPx.Width, origin.Width + dx);
            host.Height = Math.Max(minPx.Height, origin.Height + dy);
        }

        var bounds = PlacementBounds;
        _drag.IsValid = BoardRules.CanPlace(_layout.Widgets, host.Config, _drag.Candidate, bounds.Columns, bounds.Rows);
        ShowPreview(_drag.Candidate, _drag.IsValid);
    }

    private void OnDragCompleted(WidgetHost host)
    {
        if (_drag?.Host != host)
            return;

        var changed = _drag.IsValid && _drag.Candidate != _drag.Origin;
        if (changed)
            host.Config.Bounds = _drag.Candidate;
        _drag = null;
        Preview.Visibility = Visibility.Collapsed;
        Canvas.SetZIndex(host, 0);
        Place(host, host.Config.Bounds);

        if (changed)
        {
            UpdateSurface();
            LayoutEdited?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnDeleteRequested(WidgetHost host)
    {
        if (_layout is null)
            return;
        _layout.Widgets.Remove(host.Config);
        _hosts.Remove(host);
        Surface.Children.Remove(host);
        UpdateSurface();
        LayoutEdited?.Invoke(this, EventArgs.Empty);
    }

    private void ShowPreview(GridRect bounds, bool valid)
    {
        var px = _geometry.ToPixels(bounds);
        Canvas.SetLeft(Preview, px.X);
        Canvas.SetTop(Preview, px.Y);
        Preview.Width = px.Width;
        Preview.Height = px.Height;
        Preview.Fill = valid ? ValidFill : InvalidFill;
        Preview.Stroke = valid ? ValidStroke : InvalidStroke;
        Preview.Visibility = Visibility.Visible;
    }
}
