using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using NockMon.Core.Layout;
using Windows.Foundation;
using Windows.UI;

namespace NockMon.Widgets;

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
        if (!Arrange(size, cells.Count, fixedColumns, out var columns, out var cellWidth, out var cellHeight))
            return;

        var border = Palette.CellBorder(theme);
        var text = Palette.Text(theme);
        var subtle = Palette.SubtleText(theme);
        var showText = cellHeight >= 22 && cellWidth >= 40;

        // One path per layer for the whole grid (fills, overlay fills, their edges, cell borders): a handful of
        // geometries per frame whatever the number of cells.
        using var fills = new CanvasPathBuilder(ds);
        using var strokes = stroke is null ? null : new CanvasPathBuilder(ds);
        using var overlayFills = overlay is null ? null : new CanvasPathBuilder(ds);
        using var overlayStrokes = overlay is null || overlayStroke is null ? null : new CanvasPathBuilder(ds);
        using var borders = new CanvasPathBuilder(ds);
        for (var i = 0; i < cells.Count; i++)
        {
            var x = (i % columns) * (cellWidth + Gap);
            var y = (i / columns) * (cellHeight + Gap);
            var cell = new Rect(x, y, cellWidth, cellHeight);
            var series = cells[i];
            ChartRenderer.AddArea(fills, strokes, cell, series.Values.AsSpan(0, series.Count), windowSamples, yMax);
            if (overlayFills is not null && i < overlay!.Count)
                ChartRenderer.AddArea(overlayFills, overlayStrokes, cell, overlay[i].Values.AsSpan(0, overlay[i].Count), windowSamples, yMax);
            // Inset half a pixel so the 1 px line is crisp; drawn last, over the area's edge.
            var (left, top, right, bottom) = (x + 0.5f, y + 0.5f, x + cellWidth - 0.5f, y + cellHeight - 0.5f);
            borders.BeginFigure(left, top);
            borders.AddLine(right, top);
            borders.AddLine(right, bottom);
            borders.AddLine(left, bottom);
            borders.EndFigure(CanvasFigureLoop.Closed);
        }
        Fill(ds, fills, fill);
        Fill(ds, overlayFills, overlayFill);
        Stroke(ds, strokes, stroke);
        Stroke(ds, overlayStrokes, overlayStroke);
        Stroke(ds, borders, border);

        if (!showText)
            return;
        for (var i = 0; i < cells.Count; i++)
        {
            var x = (i % columns) * (cellWidth + Gap);
            var y = (i / columns) * (cellHeight + Gap);
            var series = cells[i];
            ds.DrawText(series.Label, new Rect(x + 3, y + 1, cellWidth * 0.65, 14), subtle, boldText ? BoldLabelFormat : LabelFormat);
            ds.DrawText(formatValue(series), new Rect(x, y + 1, cellWidth - 3, 14), text, boldText ? BoldValueFormat : ValueFormat);
        }
    }

    /// <returns>False when the cells are too small to draw.</returns>
    private static bool Arrange(Size size, int count, int fixedColumns, out int columns, out float cellWidth, out float cellHeight)
    {
        (columns, var rows) = GridArrangement.Arrange(count, size.Width, size.Height, fixedColumns);
        cellWidth = cellHeight = 0;
        if (columns == 0)
            return false;
        cellWidth = (float)((size.Width - Gap * (columns - 1)) / columns);
        cellHeight = (float)((size.Height - Gap * (rows - 1)) / rows);
        return cellWidth >= 4 && cellHeight >= 4;
    }

    [ThreadStatic] private static SeriesData[]? _hoverOne, _hoverTwo;

    /// <summary>
    /// Hover readout (<see cref="ChartRenderer.DrawHover"/>) on the cell under <paramref name="pointer"/>: its series and
    /// overlay; the box's first line is the <paramref name="title"/> of the cell's series, if any, and the time.
    /// </summary>
    public static void DrawHover(CanvasDrawingSession ds, Size size, IReadOnlyList<SeriesData> cells, IReadOnlyList<SeriesData>? overlay,
        SampleTimes times, int windowSamples, double yMax, int fixedColumns, Point pointer, Rect bounds, ElementTheme theme,
        Func<SeriesData, float, string> format, Func<SeriesData, string>? title = null, Func<SeriesData, string>? label = null)
    {
        if (pointer.X < 0 || pointer.Y < 0 || !Arrange(size, cells.Count, fixedColumns, out var columns, out var cellWidth, out var cellHeight))
            return;
        int column = (int)(pointer.X / (cellWidth + Gap)), row = (int)(pointer.Y / (cellHeight + Gap));
        var i = row * columns + column;
        if (column >= columns || i >= cells.Count)
            return;
        var cell = new Rect(column * (cellWidth + Gap), row * (cellHeight + Gap), cellWidth, cellHeight);
        var layout = new ChartLayout(cell, 0, yMax, (float)(cell.Width / Math.Max(1, windowSamples - 1)), windowSamples);
        SeriesData[] series;
        if (overlay is not null && i < overlay.Count)
        {
            series = _hoverTwo ??= new SeriesData[2];
            series[1] = overlay[i];
        }
        else
        {
            series = _hoverOne ??= new SeriesData[1];
        }
        series[0] = cells[i];
        ChartRenderer.DrawHover(ds, layout, series, times, pointer, bounds, new ChartStyle(false, false, false, theme), format,
            title?.Invoke(cells[i]), label);
        Array.Clear(series); // No reference kept to a widget's series.
    }

    private static void Fill(CanvasDrawingSession ds, CanvasPathBuilder? path, Color color)
    {
        if (path is null)
            return;
        using var geometry = CanvasGeometry.CreatePath(path);
        ds.FillGeometry(geometry, color);
    }

    private static void Stroke(CanvasDrawingSession ds, CanvasPathBuilder? path, Color? color)
    {
        if (path is null || color is not { } c)
            return;
        using var geometry = CanvasGeometry.CreatePath(path);
        ds.DrawGeometry(geometry, c, 1);
    }
}
