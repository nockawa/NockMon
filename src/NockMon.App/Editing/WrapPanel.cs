using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NockMon.Editing;

/// <summary>
/// Lays its children out left to right and wraps to a new line when the width runs out (dialogs are narrow; a horizontal
/// StackPanel gives its children infinite width and gets clipped). A child wider than the panel gets the panel's width.
/// </summary>
internal sealed partial class WrapPanel : Panel
{
    /// <summary>Gap between two children of a line.</summary>
    public double Spacing { get; init; } = 8;

    /// <summary>Gap between two lines.</summary>
    public double LineSpacing { get; init; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        var constraint = new Size(availableSize.Width, double.PositiveInfinity);
        foreach (var child in Children)
            child.Measure(constraint);
        return Layout(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, lineHeight = 0, maxWidth = 0;
        var lineStart = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (child.Visibility == Visibility.Collapsed)
                continue;
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width)
            {
                if (arrange)
                    ArrangeLine(lineStart, i, y, lineHeight);
                y += lineHeight + LineSpacing;
                x = lineHeight = 0;
                lineStart = i;
            }
            x += size.Width + Spacing;
            lineHeight = Math.Max(lineHeight, size.Height);
            maxWidth = Math.Max(maxWidth, x - Spacing);
        }
        if (arrange)
            ArrangeLine(lineStart, Children.Count, y, lineHeight);
        return new Size(double.IsInfinity(width) ? maxWidth : Math.Min(maxWidth, width), y + lineHeight);
    }

    private void ArrangeLine(int start, int end, double y, double height)
    {
        double x = 0;
        for (var i = start; i < end; i++)
        {
            var child = Children[i];
            if (child.Visibility == Visibility.Collapsed)
                continue;
            child.Arrange(new Rect(x, y, child.DesiredSize.Width, height));
            x += child.DesiredSize.Width + Spacing;
        }
    }
}
