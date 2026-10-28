using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyCpuMonitor.Core.Layout;
using Windows.Foundation;

namespace MyCpuMonitor.Board;

public enum DragKind
{
    Move,
    Resize,
}

/// <summary>
/// Widget chrome: title, content, and in edit mode an overlay that turns pointer drags into
/// <see cref="DragDelta"/> events (deltas relative to <see cref="DragReference"/>, so they stay stable while the host moves).
/// </summary>
public sealed partial class WidgetHost : UserControl
{
    private static readonly InputCursor MoveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private static readonly InputCursor ResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);

    private DragKind? _dragKind;
    private Point _dragStart;
    private bool _isEditing;

    public WidgetHost(WidgetConfig config, UIElement content, UIElement dragReference)
    {
        InitializeComponent();
        Config = config;
        DragReference = dragReference;
        Body.Content = content;
        Widget = content as Widgets.IMonitorWidget;
        UpdateTitle();
    }

    public WidgetConfig Config { get; }
    public Widgets.IMonitorWidget? Widget { get; }
    public UIElement DragReference { get; }
    public bool IsDragging => _dragKind is not null;

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            _isEditing = value;
            EditOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            ProtectedCursor = value ? MoveCursor : null;
        }
    }

    public event Action<WidgetHost, DragKind>? DragStarted;
    public event Action<WidgetHost, double, double>? DragDelta;
    public event Action<WidgetHost>? DragCompleted;
    public event Action<WidgetHost>? DeleteRequested;
    public event Action<WidgetHost>? ConfigureRequested;

    public void UpdateTitle()
    {
        TitleText.Text = Config.Title ?? "";
        TitleText.Visibility = string.IsNullOrEmpty(Config.Title) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnMoveSurfacePressed(object sender, PointerRoutedEventArgs e) => BeginDrag(DragKind.Move, e);

    private void OnResizeGripPressed(object sender, PointerRoutedEventArgs e) => BeginDrag(DragKind.Resize, e);

    private void BeginDrag(DragKind kind, PointerRoutedEventArgs e)
    {
        if (_dragKind is not null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (!EditOverlay.CapturePointer(e.Pointer))
            return;
        _dragKind = kind;
        _dragStart = e.GetCurrentPoint(DragReference).Position;
        e.Handled = true;
        DragStarted?.Invoke(this, kind);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragKind is null)
            return;
        var p = e.GetCurrentPoint(DragReference).Position;
        DragDelta?.Invoke(this, p.X - _dragStart.X, p.Y - _dragStart.Y);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragKind is null)
            return;
        EditOverlay.ReleasePointerCapture(e.Pointer); // Raises PointerCaptureLost, which completes the drag.
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_dragKind is null)
            return;
        _dragKind = null;
        DragCompleted?.Invoke(this);
    }

    private void OnMoveSurfaceEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_isEditing && _dragKind is null)
            ProtectedCursor = MoveCursor;
    }

    private void OnResizeGripEntered(object sender, PointerRoutedEventArgs e) => ProtectedCursor = ResizeCursor;

    private void OnResizeGripExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dragKind != DragKind.Resize)
            ProtectedCursor = _isEditing ? MoveCursor : null;
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke(this);

    private void OnConfigureClick(object sender, RoutedEventArgs e) => ConfigureRequested?.Invoke(this);
}
