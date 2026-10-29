namespace MyCpuMonitor.Core.Layout;

/// <summary>Stacking and unstacking widgets of a layout (edit mode).</summary>
public static class WidgetStacking
{
    /// <summary>
    /// True if <paramref name="dragged"/> can be dropped onto <paramref name="target"/>: two different widgets of the
    /// layout, and the target area is large enough for every resulting item.
    /// </summary>
    public static bool CanStack(LayoutDocument layout, WidgetConfig dragged, WidgetConfig target)
    {
        if (ReferenceEquals(dragged, target) || !layout.Widgets.Contains(dragged) || !layout.Widgets.Contains(target))
            return false;
        var min = WidgetCatalog.MinSize(dragged);
        return target.Bounds.ColumnSpan >= min.Columns && target.Bounds.RowSpan >= min.Rows;
    }

    /// <summary>
    /// Drops <paramref name="dragged"/> onto <paramref name="target"/>: the target becomes (or already is) a stack at its
    /// bounds, the dragged widget (or the items of a dragged stack) is appended and shown.
    /// </summary>
    /// <returns>The resulting stack.</returns>
    public static StackWidgetConfig Stack(LayoutDocument layout, WidgetConfig dragged, WidgetConfig target)
    {
        if (!CanStack(layout, dragged, target))
            throw new InvalidOperationException("These widgets cannot be stacked.");

        var stack = target as StackWidgetConfig;
        if (stack is null)
        {
            stack = new StackWidgetConfig { Bounds = target.Bounds, Items = [target] };
            layout.Widgets[layout.Widgets.IndexOf(target)] = stack;
        }
        layout.Widgets.Remove(dragged);
        var first = stack.Items.Count;
        stack.Items.AddRange(dragged is StackWidgetConfig s ? s.Items : [dragged]);
        foreach (var item in stack.Items)
            item.Bounds = stack.Bounds;
        stack.CurrentIndex = first;
        return stack;
    }

    /// <summary>
    /// Moves item <paramref name="index"/> out of <paramref name="stack"/> to <paramref name="spot"/>. A stack left with
    /// a single item is replaced by that item.
    /// </summary>
    /// <returns>The unstacked widget.</returns>
    public static WidgetConfig Unstack(LayoutDocument layout, StackWidgetConfig stack, int index, GridRect spot)
    {
        var position = layout.Widgets.IndexOf(stack);
        if (position < 0)
            throw new InvalidOperationException("The stack is not part of the layout.");

        var item = stack.Items[index];
        stack.Items.RemoveAt(index);
        item.Bounds = spot;
        layout.Widgets.Add(item);
        stack.CurrentIndex = Math.Min(stack.CurrentIndex, stack.Items.Count - 1);
        if (stack.Items.Count == 1)
        {
            var last = stack.Items[0];
            last.Bounds = stack.Bounds;
            layout.Widgets[position] = last;
        }
        return item;
    }
}
