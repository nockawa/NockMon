using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace NockMon.Widgets;

/// <summary>
/// Hover readout over a chart (<see cref="ChartRenderer.DrawHover"/>): tracks the pointer over a surface and draws on an
/// overlay canvas, so the chart isn't redrawn when the pointer moves. The overlay may be larger than the chart (a
/// sparkline's box spills over its widget): the draw callback gets the pointer and the overlay's bounds in the chart's
/// coordinates.
/// </summary>
internal sealed class ChartHover
{
    private readonly UIElement _chart;
    private readonly Action<CanvasDrawingSession, Point, Rect> _draw;
    private Point? _pointer;

    public CanvasControl Overlay { get; } = new() { IsHitTestVisible = false };

    /// <param name="surface">Takes the pointer; given a transparent background if it has none, so its whole area does
    /// (not only the drawn pixels).</param>
    public ChartHover(Panel surface, UIElement chart, Action<CanvasDrawingSession, Point, Rect> draw)
    {
        _chart = chart;
        _draw = draw;
        surface.Background ??= new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        surface.PointerMoved += (_, e) =>
        {
            _pointer = e.GetCurrentPoint(_chart).Position;
            Overlay.Invalidate();
        };
        surface.PointerExited += (_, _) => Clear();
        surface.PointerCanceled += (_, _) => Clear();
        Overlay.Draw += OnDraw;
    }

    /// <summary>The chart was redrawn (new data, resize): the readout under the pointer follows.</summary>
    public void ChartDrawn()
    {
        if (_pointer is not null)
            Overlay.Invalidate();
    }

    private void Clear()
    {
        _pointer = null;
        Overlay.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_pointer is not { } pointer)
            return;
        var offset = _chart.TransformToVisual(Overlay).TransformPoint(default);
        var ds = args.DrawingSession;
        ds.Transform = Matrix3x2.CreateTranslation((float)offset.X, (float)offset.Y);
        _draw(ds, pointer, new Rect(-offset.X, -offset.Y, sender.ActualWidth, sender.ActualHeight));
    }
}
