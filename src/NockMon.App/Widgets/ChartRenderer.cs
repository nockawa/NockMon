using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using NockMon.Core.Metrics;
using NockMon.Monitoring;
using Windows.Foundation;

namespace NockMon.Widgets;

/// <param name="ZeroBased">Y axis from 0 (default). False: fitted to the data's range, so a small change of a large value
/// (a folder's size) shows; not with <paramref name="Stacked"/>.</param>
public readonly record struct ChartStyle(bool Stacked, bool ShowLegend, bool ShowGrid, ElementTheme Theme, bool ZeroBased = true);

/// <summary>Where <see cref="ChartRenderer.Draw"/> put the plot, for an overlay drawn over it (<see cref="ChartRenderer.DrawHover"/>).</summary>
public readonly record struct ChartLayout(Rect Plot, double YMin, double YMax, float Dx, int WindowSamples)
{
    public bool IsEmpty => WindowSamples < 2 || Plot.Height < 4;

    public float Right => (float)(Plot.X + Plot.Width);

    /// <summary>Y of <paramref name="value"/>, clamped to the plot (a clipped spike sits on the top edge).</summary>
    public float Y(double value) =>
        (float)Math.Clamp(Plot.Y + Plot.Height - (value - YMin) * Plot.Height / (YMax - YMin), Plot.Y, Plot.Y + Plot.Height);
}

/// <summary>
/// Time-series rendering: newest sample at the right edge, a window of <c>windowSamples</c> across the width
/// (partial history starts mid-chart). Y axis from 0 to a nice ceiling that a few spikes don't set
/// (<see cref="AxisScale.SpikeAwareCeiling"/>: they are clipped, with a caret and the highest one's value); CPU in machine
/// scale is fixed at 100%.
/// </summary>
public static class ChartRenderer
{
    private static readonly CanvasTextFormat LegendFormat = new() { FontSize = 11, FontFamily = "Segoe UI", WordWrapping = CanvasWordWrapping.NoWrap };
    private static readonly CanvasTextFormat AxisFormat = new() { FontSize = 10, FontFamily = "Segoe UI", WordWrapping = CanvasWordWrapping.NoWrap };

    public static ChartLayout Draw(CanvasDrawingSession ds, Rect area, IReadOnlyList<SeriesData> series, int windowSamples,
        ChartStyle style, MonitorService monitor)
    {
        if (series.Count == 0 || area.Width < 8 || area.Height < 8)
            return default;

        var plot = area;
        if (style.ShowLegend)
        {
            var legendHeight = DrawLegend(ds, area, series, style, monitor);
            plot = new Rect(area.X, area.Y + legendHeight + 2, area.Width, Math.Max(0, area.Height - legendHeight - 2));
        }
        if (plot.Height < 4)
            return default;

        var unit = series[0].Source.Unit;
        var (yMin, yMax) = style.ZeroBased || style.Stacked ? (0d, ComputeMax(series, unit, style.Stacked, monitor)) : FitRange(series);

        if (style.ShowGrid)
            DrawGrid(ds, plot, yMin, yMax, unit, style.Theme, monitor);

        var dx = (float)(plot.Width / Math.Max(1, windowSamples - 1));
        Span<float> stackBase = windowSamples <= 8192 ? stackalloc float[windowSamples] : new float[windowSamples];
        stackBase.Clear();
        var highest = new ClippedPeak(0, 0, default);

        for (var i = 0; i < series.Count; i++)
        {
            var s = series[i];
            if (s.Count < 2)
                continue;
            var values = s.Values.AsSpan(0, s.Count);
            // Newest sample at the right edge: a shorter series (a folder's, since its first scan) stacks on the last slots.
            var baseline = stackBase[^s.Count..];
            var n = TopPoints(plot, values, baseline, style.Stacked, dx, yMin, yMax, out var top, out var bottom);

            // Area: one polygon (top points, then the baseline backwards): a single interop call.
            var polygon = Polygon(2 * n);
            top.AsSpan(0, n).CopyTo(polygon);
            for (var k = 0; k < n; k++)
                polygon[2 * n - 1 - k] = bottom[k];
            Pad(polygon, 2 * n);
            using (var area2 = CanvasGeometry.CreatePolygon(ds, polygon))
                ds.FillGeometry(area2, Palette.WithAlpha(s.Color, style.Stacked ? (byte)0x70 : (byte)0x28));

            // Line: an open figure needs the path builder, over at most one point per pixel column.
            using (var builder = new CanvasPathBuilder(ds))
            {
                builder.BeginFigure(top[0]);
                for (var k = 1; k < n; k++)
                    builder.AddLine(top[k]);
                builder.EndFigure(CanvasFigureLoop.Open);
                using var line = CanvasGeometry.CreatePath(builder);
                ds.DrawGeometry(line, s.Color, 1.5f);
            }

            MarkClipped(ds, plot, values, baseline, style.Stacked, dx, yMax, s.Color, ref highest);
            if (style.Stacked)
            {
                for (var k = 0; k < values.Length; k++)
                    baseline[k] += values[k];
            }
        }

        if (highest.Value > 0 && style.ShowGrid)
        {
            using var label = new CanvasTextLayout(ds, $"peak {monitor.Format(highest.Value, unit)}", AxisFormat, 10_000, 14);
            var width = (float)label.LayoutBounds.Width;
            var right = (float)(plot.X + plot.Width);
            var x = highest.X + 6 + width <= right ? highest.X + 6 : highest.X - 6 - width;
            ds.DrawTextLayout(label, new Vector2(Math.Max((float)plot.X, x), (float)plot.Y), highest.Color);
        }
        return new ChartLayout(plot, yMin, yMax, dx, windowSamples);
    }

    private readonly record struct ClippedPeak(float X, float Value, Windows.UI.Color Color);

    [ThreadStatic] private static Vector2[]? _caret;

    /// <summary>
    /// A caret on the top edge over each run of samples above the axis (at the run's peak); the highest peak of all series
    /// goes to <paramref name="highest"/>, for its label.
    /// </summary>
    private static void MarkClipped(CanvasDrawingSession ds, Rect plot, ReadOnlySpan<float> values, ReadOnlySpan<float> baseline, bool stacked,
        float dx, double yMax, Windows.UI.Color color, ref ClippedPeak highest)
    {
        var startX = (float)(plot.X + plot.Width) - (values.Length - 1) * dx;
        var top = (float)plot.Y;
        var caret = _caret ??= new Vector2[3];
        int peak = -1;
        float peakValue = 0;
        for (var k = 0; k <= values.Length; k++)
        {
            var v = k < values.Length ? values[k] + (stacked ? baseline[k] : 0) : float.NegativeInfinity;
            if (v > yMax)
            {
                if (peak < 0 || v > peakValue)
                    (peak, peakValue) = (k, v);
                continue;
            }
            if (peak < 0)
                continue;
            var x = startX + peak * dx;
            caret[0] = new Vector2(x - 4, top + 6);
            caret[1] = new Vector2(x + 4, top + 6);
            caret[2] = new Vector2(x, top);
            using (var geometry = CanvasGeometry.CreatePolygon(ds, caret))
                ds.FillGeometry(geometry, color);
            if (peakValue > highest.Value)
                highest = new ClippedPeak(x, peakValue, color);
            peak = -1;
        }
    }

    /// <summary>
    /// Hover readout over a chart drawn with <paramref name="layout"/>: a light vertical bar on the sample nearest to
    /// <paramref name="pointer"/>, a dot on each series and a box with the sample's time and the series' values.
    /// </summary>
    public static void DrawHover(CanvasDrawingSession ds, ChartLayout layout, IReadOnlyList<SeriesData> series, SampleTimes times,
        Point pointer, ChartStyle style, MonitorService monitor)
    {
        if (layout.IsEmpty || series.Count == 0)
            return;
        var newest = 0;
        for (var i = 0; i < series.Count; i++)
            newest = Math.Max(newest, series[i].Count);
        // Samples back from the newest (right edge); none left of the oldest.
        var back = Math.Max(0, (int)Math.Round((layout.Right - pointer.X) / layout.Dx));
        if (back >= Math.Min(newest, layout.WindowSamples))
            return;

        var plot = layout.Plot;
        var x = layout.Right - back * layout.Dx;
        ds.DrawLine(x, (float)plot.Y, x, (float)(plot.Y + plot.Height), Palette.Crosshair(style.Theme), 1);

        var panel = Palette.Panel(style.Theme);
        double stack = 0;
        for (var i = 0; i < series.Count; i++)
        {
            var s = series[i];
            if (s.Error is not null || s.Count <= back)
                continue;
            var v = s.Values[s.Count - 1 - back];
            stack += v;
            var y = layout.Y(style.Stacked ? stack : v);
            ds.FillCircle(x, y, 4, panel);
            ds.FillCircle(x, y, 2.75f, s.Color);
        }

        // The box: the time, then one line per series.
        const float lineHeight = 15, pad = 6, swatch = 11;
        var time = times.Count > back ? times.Values[times.Count - 1 - back].ToLocalTime().ToString("T", CultureInfo.CurrentCulture) : "";
        var lines = new CanvasTextLayout[series.Count + 1];
        try
        {
            lines[0] = new CanvasTextLayout(ds, time, LegendFormat, 10_000, lineHeight);
            var width = (float)lines[0].LayoutBounds.Width;
            for (var i = 0; i < series.Count; i++)
            {
                var s = series[i];
                var value = s.Error is not null || s.Count <= back ? "–" : monitor.Format(s.Values[s.Count - 1 - back], s.Source.Unit);
                lines[i + 1] = new CanvasTextLayout(ds, $"{s.Label}  {value}", LegendFormat, 10_000, lineHeight);
                width = Math.Max(width, swatch + (float)lines[i + 1].LayoutBounds.Width);
            }
            float w = width + 2 * pad, h = lines.Length * lineHeight + 2 * pad;
            var bx = x + 10 + w <= layout.Right ? x + 10 : Math.Max((float)plot.X, x - 10 - w);
            var by = (float)Math.Clamp(pointer.Y - h / 2, 0, Math.Max(0, plot.Y + plot.Height - h));
            ds.FillRoundedRectangle(bx, by, w, h, 4, 4, panel);
            ds.DrawRoundedRectangle(bx, by, w, h, 4, 4, Palette.CellBorder(style.Theme), 1);
            ds.DrawTextLayout(lines[0], new Vector2(bx + pad, by + pad), Palette.SubtleText(style.Theme));
            var text = Palette.Text(style.Theme);
            for (var i = 0; i < series.Count; i++)
            {
                var ly = by + pad + (i + 1) * lineHeight;
                ds.FillRoundedRectangle(bx + pad, ly + 4, 8, 8, 2, 2, series[i].Color);
                ds.DrawTextLayout(lines[i + 1], new Vector2(bx + pad + swatch, ly), text);
            }
        }
        finally
        {
            foreach (var line in lines)
                line?.Dispose();
        }
    }

    [ThreadStatic] private static Vector2[]? _top;
    [ThreadStatic] private static Vector2[]? _bottom;

    /// <summary>
    /// Top and baseline points, newest at the right edge, downsampled to pixel columns: per column, the sample with the
    /// highest top (spikes stay visible) and its baseline.
    /// </summary>
    private static int TopPoints(Rect plot, ReadOnlySpan<float> values, ReadOnlySpan<float> baseline, bool stacked, float dx, double yMin, double yMax,
        out Vector2[] top, out Vector2[] bottom)
    {
        var right = (float)(plot.X + plot.Width);
        var plotBottom = (float)(plot.Y + plot.Height);
        var plotTop = (float)plot.Y;
        var scale = (float)(plot.Height / (yMax - yMin));
        var offset = (float)yMin;
        var startX = right - (values.Length - 1) * dx;
        var bucket = Math.Max(1, (int)Math.Ceiling(1 / dx));
        var capacity = (values.Length + bucket - 1) / bucket;
        top = _top is { } t && t.Length >= capacity ? t : _top = new Vector2[Math.Max(capacity, 1024)];
        bottom = _bottom is { } b && b.Length >= capacity ? b : _bottom = new Vector2[Math.Max(capacity, 1024)];

        float Y(float v) => Math.Clamp(plotBottom - (v - offset) * scale, plotTop, plotBottom);

        var n = 0;
        for (var i = 0; i < values.Length; i += bucket)
        {
            var end = Math.Min(values.Length, i + bucket);
            var best = i;
            var bestTop = values[i] + (stacked ? baseline[i] : 0);
            for (var k = i + 1; k < end; k++)
            {
                var v = values[k] + (stacked ? baseline[k] : 0);
                if (v > bestTop)
                    (best, bestTop) = (k, v);
            }
            var x = bucket == 1 ? startX + i * dx : startX + (end - 1) * dx;
            top[n] = new Vector2(x, Y(bestTop));
            bottom[n] = new Vector2(x, stacked ? Y(baseline[best]) : plotBottom);
            n++;
        }
        return n;
    }

    /// <summary>
    /// A polygon buffer of at least <paramref name="length"/> points. CreatePolygon takes the whole array: lengths are
    /// rounded up to a multiple of 64 and the caller fills the tail with <see cref="Pad"/> (repeated last vertex, a
    /// degenerate edge). The cache is then small and stops growing: one buffer per 64 points, not per exact count
    /// (which changes each sample while the history fills, and at each resize).
    /// </summary>
    private static Vector2[] Polygon(int length)
    {
        var rounded = (length + 63) & ~63;
        _polygons ??= [];
        if (!_polygons.TryGetValue(rounded, out var polygon))
            _polygons[rounded] = polygon = new Vector2[rounded];
        return polygon;
    }

    private static void Pad(Vector2[] polygon, int used) => polygon.AsSpan(used).Fill(polygon[used - 1]);

    [ThreadStatic] private static Dictionary<int, Vector2[]>? _polygons;

    /// <summary>
    /// Adds the filled area (to zero) of one series to <paramref name="fill"/>, as a closed figure, and its top edge to
    /// <paramref name="stroke"/>, as an open one: a grid of cells is then one fill and one stroke per layer, not two
    /// geometries per cell. Newest sample at the right edge, at most one point per pixel column (bucket maximum, so
    /// spikes stay visible). For small multiples.
    /// </summary>
    internal static void AddArea(CanvasPathBuilder fill, CanvasPathBuilder? stroke, Rect plot, ReadOnlySpan<float> values, int windowSamples, double yMax)
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
        var edge = Polygon(points);
        var n = 0;
        for (var i = 0; i < values.Length; i += bucket)
        {
            var end = Math.Min(values.Length, i + bucket);
            var max = values[i];
            for (var k = i + 1; k < end; k++)
                max = Math.Max(max, values[k]);
            edge[n++] = new Vector2(startX + (end - 1) * dx, Math.Max(top, bottom - max * scale));
        }

        fill.BeginFigure(edge[0]);
        for (var k = 1; k < n; k++)
            fill.AddLine(edge[k]);
        fill.AddLine(new Vector2(right, bottom));
        fill.AddLine(new Vector2(edge[0].X, bottom));
        fill.EndFigure(CanvasFigureLoop.Closed);

        if (stroke is null || n < 2)
            return;
        stroke.BeginFigure(edge[0]);
        for (var k = 1; k < n; k++)
            stroke.AddLine(edge[k]);
        stroke.EndFigure(CanvasFigureLoop.Open);
    }

    private static double ComputeMax(IReadOnlyList<SeriesData> series, MetricUnit unit, bool stacked, MonitorService monitor)
    {
        if (unit == MetricUnit.Cpu && monitor.CpuScale == CpuScale.Machine)
            return monitor.LogicalProcessors;

        // The chart's top edge per sample (the stack's sum, or the highest series), aligned on the newest sample.
        var n = 0;
        for (var i = 0; i < series.Count; i++)
            n = Math.Max(n, series[i].Count);
        if (_envelope is null || _envelope.Length < 2 * n)
            _envelope = new float[Math.Max(2 * n, 2048)];
        var envelope = _envelope.AsSpan(0, n);
        envelope.Clear();
        for (var i = 0; i < series.Count; i++) // Indexed: an interface enumerator per frame otherwise.
        {
            var s = series[i];
            var top = envelope[(n - s.Count)..];
            var values = s.Values.AsSpan(0, s.Count);
            for (var k = 0; k < values.Length; k++)
                top[k] = stacked ? top[k] + values[k] : Math.Max(top[k], values[k]);
        }
        var ceiling = AxisScale.SpikeAwareCeiling(envelope, _envelope.AsSpan(n, n), unit);
        if (unit != MetricUnit.Cpu)
            return ceiling;
        // CPUs: at least one CPU so idle charts don't amplify noise, at most the machine.
        return Math.Clamp(ceiling, 1, Math.Max(1, monitor.LogicalProcessors));
    }

    [ThreadStatic] private static float[]? _envelope;

    /// <summary>Visible values' range with a 15 % margin each side (at least 1 % of the value, so a flat line sits mid-chart), floored at 0.</summary>
    private static (double Min, double Max) FitRange(IReadOnlyList<SeriesData> series)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var s in series)
        {
            foreach (var v in s.Values.AsSpan(0, s.Count))
                (min, max) = (Math.Min(min, v), Math.Max(max, v));
        }
        if (min > max)
            return (0, 1);
        var range = Math.Max(max - min, Math.Max(Math.Abs(max) * 0.01, 1));
        return (Math.Max(0, min - range * 0.15), max + range * 0.15);
    }

    private static void DrawGrid(CanvasDrawingSession ds, Rect plot, double yMin, double yMax, MetricUnit unit, ElementTheme theme, MonitorService monitor)
    {
        var grid = Palette.Grid(theme);
        for (var i = 0; i <= 4; i++)
        {
            var y = (float)(plot.Y + plot.Height * i / 4);
            ds.DrawLine((float)plot.X, y, (float)(plot.X + plot.Width), y, grid, 1);
        }
        ds.DrawText(monitor.Format(yMax, unit), (float)plot.X + 2, (float)plot.Y + 1, Palette.SubtleText(theme), AxisFormat);
        if (plot.Height >= 60) // The middle line too, above it, when there's room for both.
            ds.DrawText(monitor.Format((yMin + yMax) / 2, unit), (float)plot.X + 2, (float)(plot.Y + plot.Height / 2) - 13, Palette.SubtleText(theme), AxisFormat);
        if (yMin > 0)
            ds.DrawText(monitor.Format(yMin, unit), (float)plot.X + 2, (float)(plot.Y + plot.Height) - 13, Palette.SubtleText(theme), AxisFormat);
    }

    /// <returns>Legend height.</returns>
    private static float DrawLegend(CanvasDrawingSession ds, Rect area, IReadOnlyList<SeriesData> series, ChartStyle style, MonitorService monitor)
    {
        float x = (float)area.X, y = (float)area.Y, lineHeight = 15;
        var maxX = (float)(area.X + area.Width);
        var text = Palette.Text(style.Theme);
        foreach (var s in series)
        {
            var value = s.Error is not null ? "error" : s.Count > 0 ? monitor.Format(s.Latest, s.Source.Unit) : "–";
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
