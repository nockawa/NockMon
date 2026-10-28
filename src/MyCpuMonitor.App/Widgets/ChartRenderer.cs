using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Monitoring;
using Windows.Foundation;

namespace MyCpuMonitor.Widgets;

public sealed record ChartStyle(bool Stacked, bool ShowLegend, bool ShowGrid, ElementTheme Theme);

/// <summary>
/// Time-series rendering: newest sample at the right edge, a window of <c>windowSamples</c> across the width
/// (partial history starts mid-chart). Y axis from 0 to a nice ceiling; CPU in machine scale is fixed at 100%.
/// </summary>
public static class ChartRenderer
{
    private static readonly CanvasTextFormat LegendFormat = new() { FontSize = 11, FontFamily = "Segoe UI", WordWrapping = CanvasWordWrapping.NoWrap };
    private static readonly CanvasTextFormat AxisFormat = new() { FontSize = 10, FontFamily = "Segoe UI", WordWrapping = CanvasWordWrapping.NoWrap };

    public static void Draw(CanvasDrawingSession ds, Rect area, IReadOnlyList<SeriesData> series, int windowSamples,
        ChartStyle style, MonitorService monitor)
    {
        if (series.Count == 0 || area.Width < 8 || area.Height < 8)
            return;

        var plot = area;
        if (style.ShowLegend)
        {
            var legendHeight = DrawLegend(ds, area, series, style, monitor);
            plot = new Rect(area.X, area.Y + legendHeight + 2, area.Width, Math.Max(0, area.Height - legendHeight - 2));
        }
        if (plot.Height < 4)
            return;

        var unit = series[0].Source.Info.Unit;
        var yMax = ComputeMax(series, unit, style.Stacked, monitor);

        if (style.ShowGrid)
            DrawGrid(ds, plot, yMax, unit, style.Theme, monitor);

        var dx = (float)(plot.Width / Math.Max(1, windowSamples - 1));
        Span<float> stackBase = windowSamples <= 8192 ? stackalloc float[windowSamples] : new float[windowSamples];
        stackBase.Clear();

        for (var i = 0; i < series.Count; i++)
        {
            var s = series[i];
            if (s.Count < 2)
                continue;
            var values = s.Values.AsSpan(0, s.Count);
            var baseline = stackBase[..s.Count];
            using var line = BuildPath(ds, plot, values, baseline, style.Stacked, dx, yMax, closeToBaseline: false);
            using var area2 = BuildPath(ds, plot, values, baseline, style.Stacked, dx, yMax, closeToBaseline: true);
            ds.FillGeometry(area2, Palette.WithAlpha(s.Color, style.Stacked ? (byte)0x70 : (byte)0x28));
            ds.DrawGeometry(line, s.Color, 1.5f);
            if (style.Stacked)
            {
                for (var k = 0; k < values.Length; k++)
                    baseline[k] += values[k];
            }
        }
    }

    // CreatePolygon needs an exact-length array; cells of a widget share a width, so a per-length cache is allocation-free.
    [ThreadStatic] private static Dictionary<int, Vector2[]>? _polygons;

    /// <summary>
    /// Filled area (to zero) of one series, newest sample at the right edge, built as a single polygon (one interop call)
    /// and downsampled to at most one point per pixel column (bucket maximum, so spikes stay visible). For small multiples.
    /// </summary>
    internal static void FillArea(CanvasDrawingSession ds, Rect plot, ReadOnlySpan<float> values, int windowSamples, double yMax, Windows.UI.Color color)
    {
        if (values.Length < 2 || plot.Width < 2 || plot.Height < 2)
            return;

        var right = (float)(plot.X + plot.Width);
        var bottom = (float)(plot.Y + plot.Height);
        var top = (float)plot.Y;
        var scale = (float)(plot.Height / yMax);
        var dx = (float)(plot.Width / Math.Max(1, windowSamples - 1));
        var startX = right - (values.Length - 1) * dx;

        // Samples per pixel column (>= 1).
        var bucket = Math.Max(1, (int)Math.Ceiling(1 / dx));
        var points = (values.Length + bucket - 1) / bucket;
        _polygons ??= [];
        if (!_polygons.TryGetValue(points + 2, out var polygon))
            _polygons[points + 2] = polygon = new Vector2[points + 2];

        var n = 0;
        for (var i = 0; i < values.Length; i += bucket)
        {
            var end = Math.Min(values.Length, i + bucket);
            var max = values[i];
            for (var k = i + 1; k < end; k++)
                max = Math.Max(max, values[k]);
            polygon[n++] = new Vector2(startX + (end - 1) * dx, Math.Max(top, bottom - max * scale));
        }
        polygon[n++] = new Vector2(right, bottom);
        polygon[n++] = new Vector2(polygon[0].X, bottom);

        using var geometry = CanvasGeometry.CreatePolygon(ds, polygon);
        ds.FillGeometry(geometry, color);
    }

    /// <summary>Area (filled to zero) and line of one series in <paramref name="plot"/>, newest sample at the right edge.</summary>
    internal static void DrawSeries(CanvasDrawingSession ds, Rect plot, ReadOnlySpan<float> values, int windowSamples, double yMax,
        Windows.UI.Color color, byte fillAlpha, float lineWidth)
    {
        if (values.Length < 2 || plot.Width < 2 || plot.Height < 2)
            return;
        var dx = (float)(plot.Width / Math.Max(1, windowSamples - 1));
        using var area = BuildPath(ds, plot, values, default, false, dx, yMax, closeToBaseline: true);
        ds.FillGeometry(area, Palette.WithAlpha(color, fillAlpha));
        if (lineWidth > 0)
        {
            using var line = BuildPath(ds, plot, values, default, false, dx, yMax, closeToBaseline: false);
            ds.DrawGeometry(line, color, lineWidth);
        }
    }

    private static double ComputeMax(IReadOnlyList<SeriesData> series, MetricUnit unit, bool stacked, MonitorService monitor)
    {
        if (unit == MetricUnit.Cpu && monitor.CpuScale == CpuScale.Machine)
            return monitor.LogicalProcessors;

        double max = 0;
        if (stacked)
        {
            var n = series.Max(s => s.Count);
            for (var k = 0; k < n; k++)
            {
                double sum = 0;
                foreach (var s in series)
                {
                    var offset = n - s.Count; // Align on the newest sample.
                    if (k >= offset)
                        sum += s.Values[k - offset];
                }
                max = Math.Max(max, sum);
            }
        }
        else
        {
            foreach (var s in series)
            {
                foreach (var v in s.Values.AsSpan(0, s.Count))
                    max = Math.Max(max, v);
            }
        }
        if (unit != MetricUnit.Cpu)
            return AxisScale.NiceCeiling(max, unit);
        // CPUs: at least one CPU so idle charts don't amplify noise, at most the machine.
        return Math.Clamp(AxisScale.NiceCeiling(max, unit), 1, Math.Max(1, monitor.LogicalProcessors));
    }

    private static CanvasGeometry BuildPath(ICanvasResourceCreator rc, Rect plot, ReadOnlySpan<float> values, ReadOnlySpan<float> baseline,
        bool stacked, float dx, double yMax, bool closeToBaseline)
    {
        var right = (float)(plot.X + plot.Width);
        var startX = right - (values.Length - 1) * dx;
        var bottom = (float)(plot.Y + plot.Height);
        var scale = (float)(plot.Height / yMax);

        float Y(float v) => Math.Max((float)plot.Y, bottom - v * scale);

        using var builder = new CanvasPathBuilder(rc);
        builder.BeginFigure(startX, Y(values[0] + (stacked ? baseline[0] : 0)));
        for (var k = 1; k < values.Length; k++)
            builder.AddLine(startX + k * dx, Y(values[k] + (stacked ? baseline[k] : 0)));

        if (closeToBaseline)
        {
            for (var k = values.Length - 1; k >= 0; k--)
                builder.AddLine(startX + k * dx, Y(stacked ? baseline[k] : 0));
            builder.EndFigure(CanvasFigureLoop.Closed);
        }
        else
        {
            builder.EndFigure(CanvasFigureLoop.Open);
        }
        return CanvasGeometry.CreatePath(builder);
    }

    private static void DrawGrid(CanvasDrawingSession ds, Rect plot, double yMax, MetricUnit unit, ElementTheme theme, MonitorService monitor)
    {
        var grid = Palette.Grid(theme);
        for (var i = 0; i <= 4; i++)
        {
            var y = (float)(plot.Y + plot.Height * i / 4);
            ds.DrawLine((float)plot.X, y, (float)(plot.X + plot.Width), y, grid, 1);
        }
        ds.DrawText(monitor.Format(yMax, unit), (float)plot.X + 2, (float)plot.Y + 1, Palette.SubtleText(theme), AxisFormat);
    }

    /// <returns>Legend height.</returns>
    private static float DrawLegend(CanvasDrawingSession ds, Rect area, IReadOnlyList<SeriesData> series, ChartStyle style, MonitorService monitor)
    {
        float x = (float)area.X, y = (float)area.Y, lineHeight = 15;
        var maxX = (float)(area.X + area.Width);
        var text = Palette.Text(style.Theme);
        foreach (var s in series)
        {
            var value = s.Error is not null ? "error" : s.Count > 0 ? monitor.Format(s.Latest, s.Source.Info.Unit) : "–";
            using var layout = new CanvasTextLayout(ds, $"{s.Label}  {value}", LegendFormat, 10_000, lineHeight);
            var width = (float)layout.LayoutBounds.Width + 14;
            if (x > area.X && x + width > maxX)
            {
                x = (float)area.X;
                y += lineHeight;
            }
            ds.FillRoundedRectangle(x, y + 4, 8, 8, 2, 2, s.Color);
            ds.DrawTextLayout(layout, new Vector2(x + 11, y), text);
            x += width + 8;
        }
        return y + lineHeight - (float)area.Y;
    }
}
