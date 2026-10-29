using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using NockMon.Core.Layout;
using Windows.System;

namespace NockMon.Widgets;

/// <summary>
/// Several widgets on one area, one shown at a time (carousel). Navigation: edge arrows shown while the pointer is over
/// the stack, position dots (clickable), mouse wheel over the arrows or dots, Ctrl+wheel anywhere. Optional auto-cycle,
/// paused while the pointer is over the stack and in edit mode. Only the shown item is refreshed: hidden items cost
/// nothing, and since their data comes from the engine history they show it complete when switched to.
/// </summary>
public sealed partial class StackWidget : UserControl, IMonitorWidget
{
    private const int MaxDots = 10;

    private readonly StackWidgetConfig _config;
    private readonly UIElement[] _items;
    private readonly Border _previous;
    private readonly Border _next;
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _counter = new() { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer? _timer;
    private bool _pointerOver;
    private bool _paused;

    public StackWidget(StackWidgetConfig config, Func<WidgetConfig, UIElement> factory)
    {
        _config = config;
        _items = config.Items.Select(factory).ToArray();
        _config.CurrentIndex = Math.Clamp(_config.CurrentIndex, 0, Math.Max(0, _items.Length - 1));

        var content = new Grid();
        foreach (var item in _items)
        {
            if (item is IMonitorWidget widget)
                widget.ConfigChanged += () => ConfigChanged?.Invoke();
            content.Children.Add(item);
        }

        _previous = Arrow("", HorizontalAlignment.Left, -1);
        _next = Arrow("", HorizontalAlignment.Right, 1);
        content.Children.Add(_previous);
        content.Children.Add(_next);

        var strip = new Grid { Height = 12, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { _dots, _counter } };
        strip.PointerWheelChanged += OnWheel;
        Grid.SetRow(strip, 1);
        var root = new Grid
        {
            RowDefinitions = { new RowDefinition(), new RowDefinition { Height = GridLength.Auto } },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // Hit-testable everywhere for enter/exit.
            Children = { content, strip },
        };
        // Ctrl+wheel anywhere, even when a scrolling item (table) handled the event.
        root.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnCtrlWheel), true);
        root.PointerEntered += (_, _) => SetPointerOver(true);
        root.PointerExited += (_, e) =>
        {
            // Exited also bubbles up from children: only leaving the stack's own bounds counts.
            var p = e.GetCurrentPoint(this).Position;
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
                SetPointerOver(false);
        };
        Content = root;

        if (config.CycleSeconds > 0)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(config.CycleSeconds) };
            _timer.Tick += (_, _) =>
            {
                if (!_pointerOver && !_paused)
                    Cycle(1);
            };
            Loaded += (_, _) => _timer.Start();
            Unloaded += (_, _) => _timer.Stop();
        }

        BuildDots();
        Show(_config.CurrentIndex, animate: false);
    }

    public event Action? ConfigChanged;

    /// <summary>Raised after the shown item changed.</summary>
    public event Action? CurrentChanged;

    public WidgetConfig Current => _config.Items[_config.CurrentIndex];

    /// <summary>Stops the auto-cycle (edit mode).</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public void Refresh()
    {
        if (_items.Length > 0 && _items[_config.CurrentIndex] is IMonitorWidget widget)
            widget.Refresh();
    }

    /// <summary>Shows the next (+1) or previous (-1) item, wrapping around.</summary>
    public void Cycle(int direction)
    {
        if (_items.Length < 2)
            return;
        Show(((_config.CurrentIndex + direction) % _items.Length + _items.Length) % _items.Length, animate: true);
    }

    private void Show(int index, bool animate)
    {
        if (_items.Length == 0)
            return;
        var changed = index != _config.CurrentIndex;
        _config.CurrentIndex = index;
        for (var i = 0; i < _items.Length; i++)
            _items[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        UpdateDots();
        Refresh(); // The item was not refreshed while hidden.

        if (animate)
            FadeIn(_items[index]);
        if (changed)
            CurrentChanged?.Invoke();
    }

    private static void FadeIn(UIElement element)
    {
        var animation = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(150) };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
        var storyboard = new Storyboard { Children = { animation } };
        storyboard.Begin();
    }

    private Border Arrow(string glyph, HorizontalAlignment side, int direction)
    {
        var arrow = new Border
        {
            Width = 20,
            Height = 48,
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(4),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = new FontIcon { Glyph = glyph, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
            Opacity = 0,
            IsHitTestVisible = false,
        };
        arrow.Tapped += (_, e) =>
        {
            Cycle(direction);
            e.Handled = true;
        };
        arrow.PointerWheelChanged += OnWheel;
        return arrow;
    }

    private void SetPointerOver(bool over)
    {
        if (_pointerOver == over)
            return;
        _pointerOver = over;
        var show = over && _items.Length > 1;
        foreach (var arrow in (ReadOnlySpan<Border>)[_previous, _next])
        {
            arrow.Opacity = show ? 0.8 : 0;
            arrow.IsHitTestVisible = show;
        }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        Cycle(e.GetCurrentPoint(this).Properties.MouseWheelDelta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private void OnCtrlWheel(object sender, PointerRoutedEventArgs e)
    {
        if ((e.KeyModifiers & VirtualKeyModifiers.Control) == 0)
            return;
        Cycle(e.GetCurrentPoint(this).Properties.MouseWheelDelta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private void BuildDots()
    {
        _dots.Children.Clear();
        var useDots = _items.Length <= MaxDots;
        _dots.Visibility = useDots ? Visibility.Visible : Visibility.Collapsed;
        _counter.Visibility = useDots ? Visibility.Collapsed : Visibility.Visible;
        if (!useDots)
            return;
        for (var i = 0; i < _items.Length; i++)
        {
            var index = i;
            // Larger transparent hit target around each 6 px dot.
            var dot = new Border
            {
                Width = 10,
                Height = 12,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Child = new Ellipse { Width = 6, Height = 6 },
            };
            dot.Tapped += (_, e) =>
            {
                Show(index, animate: index != _config.CurrentIndex);
                e.Handled = true;
            };
            _dots.Children.Add(dot);
        }
    }

    private void UpdateDots()
    {
        _counter.Text = $"{_config.CurrentIndex + 1} / {_items.Length}";
        _counter.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var other = (Brush)Application.Current.Resources["ControlStrongFillColorDisabledBrush"];
        for (var i = 0; i < _dots.Children.Count; i++)
            ((Ellipse)((Border)_dots.Children[i]).Child).Fill = i == _config.CurrentIndex ? accent : other;
    }
}
