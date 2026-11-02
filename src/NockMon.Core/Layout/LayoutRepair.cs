namespace NockMon.Core.Layout;

/// <summary>
/// Makes a layout read from a file safe to use: a damaged or hand-edited file must not crash the app later, on the UI
/// thread. Missing lists become empty, null entries and widgets without their required parts are dropped, nested
/// stacks are flattened (stacks never nest), sizes and positions are clamped (no overflow in the geometry).
/// </summary>
public static class LayoutRepair
{
    /// <summary>Far beyond any screen, far below int overflow when added.</summary>
    public const int MaxCoordinate = 10_000;
    public const int MaxSpan = 1_000;

    /// <exception cref="InvalidDataException">No name: nothing identifies the layout.</exception>
    public static void Repair(LayoutDocument layout)
    {
        if (string.IsNullOrWhiteSpace(layout.Name))
            throw new InvalidDataException("Layout without a name.");
        layout.Board ??= new BoardConfig();
        var board = layout.Board;
        board.CellSize = double.IsFinite(board.CellSize) ? Math.Clamp(board.CellSize, 16, 512) : 64;
        board.Gap = double.IsFinite(board.Gap) ? Math.Clamp(board.Gap, 0, 64) : 4;
        layout.Pages = layout.Pages.Where(p => p is not null).ToList(); // The setter turns an empty list into one page.
        foreach (var page in layout.Pages)
        {
            page.Name ??= "Main";
            page.Icon ??= LayoutPage.DefaultIcon;
            page.Widgets = Widgets(page.Widgets, inStack: false);
        }
    }

    private static List<WidgetConfig> Widgets(List<WidgetConfig>? widgets, bool inStack)
    {
        var result = new List<WidgetConfig>();
        foreach (var widget in widgets ?? [])
        {
            if (widget is StackWidgetConfig stack && inStack)
            {
                result.AddRange(Widgets(stack.Items, inStack: true)); // Flattened into the outer stack.
                continue;
            }
            if (Repair(widget))
                result.Add(widget);
        }
        return result;
    }

    /// <returns>False if the widget cannot be used (dropped).</returns>
    private static bool Repair(WidgetConfig? widget)
    {
        if (widget is null)
            return false;
        var b = widget.Bounds;
        widget.Bounds = new GridRect(Math.Clamp(b.Column, 0, MaxCoordinate), Math.Clamp(b.Row, 0, MaxCoordinate),
            Math.Clamp(b.ColumnSpan, 1, MaxSpan), Math.Clamp(b.RowSpan, 1, MaxSpan));
        switch (widget)
        {
            case StackWidgetConfig stack:
                stack.Items = Widgets(stack.Items, inStack: true);
                stack.CycleSeconds = Math.Max(0, stack.CycleSeconds);
                return stack.Items.Count > 0;
            case ChartWidgetConfig chart:
                chart.Series = (chart.Series ?? []).Where(s => s?.Source is not null).ToList();
                return true;
            case ValueListWidgetConfig list:
                list.Items = (list.Items ?? []).Where(s => s is not null).ToList();
                return true;
            case ValueWidgetConfig value:
                return value.Source is not null;
            case ProcessTableWidgetConfig table:
                table.Columns = table.Columns?.Where(c => c is not null).ToList() is { Count: > 0 } columns ? columns : new ProcessTableWidgetConfig().Columns;
                return true;
            case PluginWidgetConfig plugin:
                return !string.IsNullOrWhiteSpace(plugin.Plugin) && !string.IsNullOrWhiteSpace(plugin.Widget);
            case FolderWidgetConfig folder:
                folder.Path ??= "";
                return true;
            default:
                return true;
        }
    }
}
