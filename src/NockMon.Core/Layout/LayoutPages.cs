using NockMon.Core.Persistence;

namespace NockMon.Core.Layout;

/// <summary>Page (tab) operations on a layout. The shown page follows the page it was before the change when it still exists.</summary>
public static class LayoutPages
{
    /// <summary>Appends an empty page and shows it.</summary>
    public static LayoutPage Add(LayoutDocument layout, string name, string icon = LayoutPage.DefaultIcon)
    {
        var page = new LayoutPage { Name = name, Icon = icon };
        layout.Pages.Add(page);
        layout.ActivePage = layout.Pages.Count - 1;
        return page;
    }

    /// <summary>"Tab N" with the first N not used by a page.</summary>
    public static string NewName(LayoutDocument layout)
    {
        for (var n = layout.Pages.Count + 1; ; n++)
        {
            var name = $"Tab {n}";
            if (!layout.Pages.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)))
                return name;
        }
    }

    /// <summary>Removes a page and its widgets. The last page cannot be removed.</summary>
    public static void Remove(LayoutDocument layout, int index)
    {
        if (layout.Pages.Count <= 1)
            throw new InvalidOperationException("A layout keeps at least one page.");
        var shown = layout.CurrentPage;
        layout.Pages.RemoveAt(index);
        var keep = layout.Pages.IndexOf(shown);
        layout.ActivePage = keep >= 0 ? keep : Math.Min(index, layout.Pages.Count - 1);
    }

    /// <summary>Moves the page at <paramref name="from"/> to <paramref name="to"/>; the shown page stays shown.</summary>
    public static void Move(LayoutDocument layout, int from, int to)
    {
        var shown = layout.CurrentPage;
        var page = layout.Pages[from];
        layout.Pages.RemoveAt(from);
        layout.Pages.Insert(Math.Clamp(to, 0, layout.Pages.Count), page);
        layout.ActivePage = layout.Pages.IndexOf(shown);
    }

    /// <summary>Copy of a page (new ids everywhere) inserted after it, and shown.</summary>
    public static LayoutPage Duplicate(LayoutDocument layout, int index)
    {
        var source = layout.Pages[index];
        var copy = new LayoutPage
        {
            Name = source.Name + " (copy)",
            Icon = source.Icon,
            Widgets = source.Widgets.Select(CopyWidget).ToList(),
        };
        layout.Pages.Insert(index + 1, copy);
        layout.ActivePage = index + 1;
        return copy;
    }

    /// <summary>Deep copy of a widget with new ids (stack items included).</summary>
    public static WidgetConfig CopyWidget(WidgetConfig widget)
    {
        var clone = LayoutStore.Clone(widget);
        clone.Id = Guid.NewGuid();
        if (clone is StackWidgetConfig stack)
        {
            foreach (var item in stack.Items)
                item.Id = Guid.NewGuid();
        }
        return clone;
    }

    /// <summary>
    /// Moves <paramref name="widget"/> from the shown page to page <paramref name="target"/>: at the same place if free
    /// there, else at the first free spot within <paramref name="columns"/>×<paramref name="rows"/>.
    /// </summary>
    /// <returns>false (nothing changed) if the target page has no room or is the shown page.</returns>
    public static bool MoveWidget(LayoutDocument layout, WidgetConfig widget, int target, int columns, int rows)
    {
        if (target == layout.ActivePage || target < 0 || target >= layout.Pages.Count || !layout.Widgets.Contains(widget))
            return false;
        var destination = layout.Pages[target].Widgets;
        GridRect? spot = BoardRules.CanPlace(destination, widget, widget.Bounds, columns, rows)
            ? widget.Bounds
            : BoardRules.FindFreeSpot(destination, new GridSize(widget.Bounds.ColumnSpan, widget.Bounds.RowSpan), columns, rows);
        if (spot is null)
            return false;
        layout.Widgets.Remove(widget);
        widget.Bounds = spot.Value;
        destination.Add(widget);
        return true;
    }
}
