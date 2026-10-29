using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using MyCpuMonitor.Core.Layout;
using Windows.Foundation;
using Windows.UI;

namespace MyCpuMonitor.Widgets;

/// <summary>
/// Grid of small area graphs (per-CPU, per-GPU-engine): one single-polygon fill per series, an optional overlay series
/// per cell (e.g. kernel share), label and value text when cells are large enough, light grey cell borders.
/// </summary>
public static class SmallMultiples
{
    private const float Gap = 3;

    private static readonly CanvasTextFormat LabelFormat = Format(bold: false, right: false);
    private static readonly CanvasTextFormat ValueFormat = Format(bold: false, right: true);
    private static readonly CanvasTextFormat BoldLabelFormat = Format(bold: true, right: false);
    private static readonly CanvasTextFormat BoldValueFormat = Format(bold: true, right: true);

    private static CanvasTextFormat Format(bool bold, bool right) => new()
    {
        FontSize = 10, FontFamily = "Segoe UI", WordWrapping = CanvasWordWrapping.NoWrap,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
        HorizontalAlignment = right ? CanvasHorizontalAlignment.Right : CanvasHorizontalAlignment.Left,
    };

    public static void Draw(CanvasDrawingSession ds, Size size, IReadOnlyList<SeriesData> cells, IReadOnlyList<SeriesData>? overlay,
        int windowSamples, double yMax, int fixedColumns, Color fill, Color overlayFill, ElementTheme theme, Func<SeriesData, string> formatValue,
        Color? stroke = null, Color? overlayStroke = null, bool boldText = false)
    {
        var (columns, rows) = GridArrangement.Arrange(cells.Count, size.Width, size.Height, fixedColumns);
        if (columns == 0)
            return;
        var cellWidth = (float)((size.Width - Gap * (columns - 1)) / columns);
        var cellHeight = (float)((size.Height - Gap * (rows - 1)) / rows);
        if (cellWidth < 4 || cellHeight < 4)
            return;

        var border = Palette.CellBorder(theme);
        var text = Palette.Text(theme);
        var subtle = Palette.SubtleText(theme);
        var showText = cellHeight >= 22 && cellWidth >= 40;

        for (var i = 0; i < cells.Count; i++)
        {
            var x = (i % columns) * (cellWidth + Gap);
            var y = (i / columns) * (cellHeight + Gap);
            var cell = new Rect(x, y, cellWidth, cellHeight);
            var series = cells[i];
            ChartRenderer.FillArea(ds, cell, series.Values.AsSpan(0, series.Count), windowSamples, yMax, fill, stroke);
            if (overlay is not null && i < overlay.Count)
                ChartRenderer.FillArea(ds, cell, overlay[i].Values.AsSpan(0, overlay[i].Count), windowSamples, yMax, overlayFill, overlayStroke);
            // Border last, inset half a pixel so the 1 px line is crisp and drawn over the area's edge.
            ds.DrawRectangle(x + 0.5f, y + 0.5f, cellWidth - 1, cellHeight - 1, border, 1);

            if (showText)
            {
                ds.DrawText(series.Label, new Rect(x + 3, y + 1, cellWidth * 0.65, 14), subtle, boldText ? BoldLabelFormat : LabelFormat);
                ds.DrawText(formatValue(series), new Rect(x, y + 1, cellWidth - 3, 14), text, boldText ? BoldValueFormat : ValueFormat);
            }
        }
    }
}
