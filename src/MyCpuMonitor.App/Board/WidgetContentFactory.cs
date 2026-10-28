using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyCpuMonitor.Core.Layout;

namespace MyCpuMonitor.Board;

public static class WidgetContentFactory
{
    /// <summary>Stand-in content until the real widgets exist (phase 4).</summary>
    public static UIElement CreatePlaceholder(WidgetConfig config) => new TextBlock
    {
        Text = WidgetCatalog.DisplayName(WidgetCatalog.KindOf(config)),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.5,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
}
