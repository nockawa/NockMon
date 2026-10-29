namespace MyCpuMonitor.Core.Layout;

/// <summary>A widget kind provided by a loaded plugin: what the board, menus and docs need without the plugin's types.</summary>
public sealed record PluginWidgetInfo(
    string Plugin,
    string Widget,
    string DisplayName,
    string Summary,
    string Details,
    GridSize DefaultSize,
    GridSize MinSize,
    string PluginTitle,
    string PluginVersion,
    string PluginAuthors);

/// <summary>Widget kinds of the plugins loaded this session (registered at load, UI thread).</summary>
public static class PluginCatalog
{
    private static readonly List<PluginWidgetInfo> _widgets = [];

    public static IReadOnlyList<PluginWidgetInfo> Widgets => _widgets;

    public static void Register(PluginWidgetInfo widget)
    {
        _widgets.RemoveAll(w => Same(w, widget.Plugin, widget.Widget));
        _widgets.Add(widget);
    }

    public static PluginWidgetInfo? Find(PluginWidgetConfig config) => Find(config.Plugin, config.Widget);

    public static PluginWidgetInfo? Find(string plugin, string widget) => _widgets.FirstOrDefault(w => Same(w, plugin, widget));

    /// <summary>Tests only.</summary>
    internal static void Clear() => _widgets.Clear();

    private static bool Same(PluginWidgetInfo w, string plugin, string widget) =>
        string.Equals(w.Plugin, plugin, StringComparison.OrdinalIgnoreCase) && string.Equals(w.Widget, widget, StringComparison.Ordinal);
}
