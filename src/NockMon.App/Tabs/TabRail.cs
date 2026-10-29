using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NockMon.Core.Layout;
using Windows.Foundation;

namespace NockMon.Tabs;

/// <summary>
/// Thin vertical rail of a layout's pages: one icon each (name as tooltip), the shown one selected. Drag to reorder,
/// right-click for rename / icon / duplicate / delete, "+" to add. Raises requests; the window applies them.
/// </summary>
internal sealed partial class TabRail : UserControl
{
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        CanReorderItems = true,
        CanDragItems = true,
        AllowDrop = true,
        Padding = new Thickness(0, 4, 0, 4),
    };

    private readonly ObservableCollection<ListViewItem> _items = [];
    private readonly Button _add;
    private IReadOnlyList<LayoutPage> _pages = [];
    private bool _updating;
    private int _highlighted = -1;

    public TabRail()
    {
        _list.ItemsSource = _items;
        _list.SelectionChanged += (_, _) =>
        {
            if (!_updating && _list.SelectedIndex >= 0)
                PageSelected?.Invoke(_list.SelectedIndex);
        };
        _list.DragItemsCompleted += (_, _) => OnReordered();

        _add = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(_add, "Add a tab");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_add, "Add a tab");
        _add.Click += (_, _) => AddRequested?.Invoke();

        var root = new Grid { Width = 48 };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_add, 1);
        root.Children.Add(_list);
        root.Children.Add(_add);
        Content = root;
    }

    public event Action<int>? PageSelected;
    public event Action? AddRequested;
    public event Action<int>? RenameRequested;
    public event Action<int>? IconRequested;
    public event Action<int>? DuplicateRequested;
    public event Action<int>? DeleteRequested;
    public event Action<int, int>? MoveRequested;

    /// <summary>Rebuilds the items from <paramref name="layout"/>'s pages and selects the shown one.</summary>
    public void Show(LayoutDocument layout)
    {
        _updating = true;
        try
        {
            _pages = layout.Pages.ToList();
            _items.Clear();
            for (var i = 0; i < _pages.Count; i++)
                _items.Add(Item(_pages[i], i));
            _list.SelectedIndex = layout.ActivePage;
            _highlighted = -1;
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Index of the tab under a point in window coordinates (drop of a widget onto a tab), or null.</summary>
    public int? TabAt(Point windowPoint)
    {
        if (Visibility != Visibility.Visible)
            return null;
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (item.ActualWidth <= 0)
                continue;
            var origin = item.TransformToVisual(null).TransformPoint(default);
            if (new Rect(origin.X, origin.Y, item.ActualWidth, item.ActualHeight).Contains(windowPoint))
                return i;
        }
        return null;
    }

    /// <summary>Marks the tab a dragged widget would move to (null: none).</summary>
    public void Highlight(int? index)
    {
        var target = index ?? -1;
        if (target == _highlighted)
            return;
        if (_highlighted >= 0 && _highlighted < _items.Count)
            _items[_highlighted].ClearValue(BackgroundProperty);
        _highlighted = target;
        if (target >= 0 && target < _items.Count)
            _items[target].Background = (Brush)Application.Current.Resources["AccentFillColorTertiaryBrush"];
    }

    private ListViewItem Item(LayoutPage page, int index)
    {
        var item = new ListViewItem
        {
            Tag = page,
            Height = 44,
            MinWidth = 0,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = new FontIcon { FontFamily = FluentIcons.Font, Glyph = FluentIcons.Glyph(page.Icon), FontSize = 20 },
        };
        ToolTipService.SetToolTip(item, page.Name);
        ToolTipService.SetPlacement(item, Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Right);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, page.Name);

        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Rename…", "", () => RenameRequested?.Invoke(IndexOf(page))));
        menu.Items.Add(MenuItem("Change icon…", "", () => IconRequested?.Invoke(IndexOf(page))));
        menu.Items.Add(MenuItem("Duplicate", "", () => DuplicateRequested?.Invoke(IndexOf(page))));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Move up", "", () => Move(page, -1), enabled: index > 0));
        menu.Items.Add(MenuItem("Move down", "", () => Move(page, +1), enabled: index < _pages.Count - 1));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Delete…", "", () => DeleteRequested?.Invoke(IndexOf(page)), enabled: _pages.Count > 1));
        item.ContextFlyout = menu;
        return item;
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private int IndexOf(LayoutPage page) => _pages.ToList().IndexOf(page);

    private void Move(LayoutPage page, int delta)
    {
        var from = IndexOf(page);
        var to = from + delta;
        if (from >= 0 && to >= 0 && to < _pages.Count)
            MoveRequested?.Invoke(from, to);
    }

    /// <summary>After a drag reorder: finds the page that moved and asks to move it (the window then rebuilds the rail).</summary>
    private void OnReordered()
    {
        var order = _items.Select(i => (LayoutPage)i.Tag).ToList();
        if (order.SequenceEqual(_pages))
            return;
        var dragged = FindDragged(order);
        MoveRequested?.Invoke(IndexOf(dragged), order.IndexOf(dragged));
    }

    /// <summary>The single element whose removal makes both orders equal.</summary>
    private LayoutPage FindDragged(List<LayoutPage> order)
    {
        foreach (var candidate in _pages)
        {
            var before = _pages.Where(p => p != candidate);
            var after = order.Where(p => p != candidate);
            if (before.SequenceEqual(after))
                return candidate;
        }
        return order[0];
    }
}
