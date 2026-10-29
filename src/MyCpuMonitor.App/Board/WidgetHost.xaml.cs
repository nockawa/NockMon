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
    private static readonly InputCursor ArrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private static readonly InputCursor ResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);

    private DragKind? _dragKind;
    private Point _dragStart;
    private bool _isEditing;
    private TeachingTip? _help;

    public WidgetHost(WidgetConfig config, UIElement content, UIElement dragReference)
    {
        InitializeComponent();
        Config = config;
        DragReference = dragReference;
        Body.Content = content;
        Widget = content as Widgets.IMonitorWidget;
        if (content is Widgets.StackWidget stack)
        {
            stack.CurrentChanged += UpdateTitle;
            UnstackButton.Visibility = Visibility.Visible;
        }
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
            if (Widget is Widgets.StackWidget stack)
                stack.Paused = value;
            SetHelpVisible(false);
            _help?.IsOpen = false;
        }
    }

    public event Action<WidgetHost, DragKind>? DragStarted;
    public event Action<WidgetHost, double, double>? DragDelta;
    public event Action<WidgetHost>? DragCompleted;
    public event Action<WidgetHost>? DeleteRequested;
    public event Action<WidgetHost>? ConfigureRequested;
    public event Action<WidgetHost>? CloneRequested;
    public event Action<WidgetHost>? UnstackRequested;

    /// <summary>Pointer position at the start of the current drag, relative to <see cref="DragReference"/>.</summary>
    public Point DragStart => _dragStart;

    /// <summary>The widget shown: for a stack, its current item.</summary>
    private WidgetConfig Shown => Widget is Widgets.StackWidget stack ? stack.Current : Config;

    /// <summary>The widget's title and help; for a stack, the shown item's.</summary>
    public void UpdateTitle()
    {
        var title = Shown.Title;
        TitleText.Text = title ?? "";
        TitleText.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(HelpButton, Help.DocView.WidgetTooltip(Shown));
        _help?.IsOpen = false;
    }

    private void OnRootPointerEntered(object sender, PointerRoutedEventArgs e) => SetHelpVisible(true);

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Exited also bubbles up from children: only leaving the widget's own bounds counts.
        var p = e.GetCurrentPoint(Root).Position;
        if (p.X <= 0 || p.Y <= 0 || p.X >= Root.ActualWidth || p.Y >= Root.ActualHeight)
            SetHelpVisible(false);
    }

    private void SetHelpVisible(bool visible)
    {
        visible &= !_isEditing;
        HelpButton.Opacity = visible ? 0.8 : 0;
        HelpButton.IsHitTestVisible = visible;
    }

    /// <summary>Pins the widget's full help (light dismiss).</summary>
    private void OnHelpTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_help is null)
        {
            _help = new TeachingTip { Target = HelpButton, IsLightDismissEnabled = true, PreferredPlacement = TeachingTipPlacementMode.LeftBottom };
            Root.Children.Add(_help);
        }
        var shown = Shown;
        _help.Title = WidgetCatalog.DisplayName(shown);
        _help.Content = Help.DocView.WidgetDetails(shown);
        _help.IsOpen = true;
    }

    /// <summary>Edit mode: the overlay covers the stack's navigation, so the wheel cycles it directly.</summary>
    private void OnEditWheel(object sender, PointerRoutedEventArgs e)
    {
        if (Widget is not Widgets.StackWidget stack || _dragKind is not null)
            return;
        stack.Cycle(e.GetCurrentPoint(this).Properties.MouseWheelDelta > 0 ? -1 : 1);
        e.Handled = true;
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

    // The host's cursor applies to its whole subtree: the buttons over the move surface need it switched explicitly.
    private void OnButtonsEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_dragKind is null)
            ProtectedCursor = ArrowCursor;
    }

    private void OnButtonsExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dragKind is null)
            ProtectedCursor = _isEditing ? MoveCursor : null;
    }

    private void OnResizeGripEntered(object sender, PointerRoutedEventArgs e) => ProtectedCursor = ResizeCursor;

    private void OnResizeGripExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dragKind != DragKind.Resize)
            ProtectedCursor = _isEditing ? MoveCursor : null;
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke(this);

    private void OnConfigureClick(object sender, RoutedEventArgs e) => ConfigureRequested?.Invoke(this);

    private void OnCloneClick(object sender, RoutedEventArgs e) => CloneRequested?.Invoke(this);

    private void OnUnstackClick(object sender, RoutedEventArgs e) => UnstackRequested?.Invoke(this);
}
