namespace NockMon.Core.Layout;

public readonly record struct PixelRect(double X, double Y, double Width, double Height);

/// <summary>
/// Cell ↔ pixel mapping (effective pixels). Cell i spans [Gap + i·Pitch, Gap + i·Pitch + CellSize];
/// a widget spanning n cells also covers the n-1 inner gaps.
/// </summary>
public readonly record struct BoardGeometry(double CellSize, double Gap)
{
    public BoardGeometry(BoardConfig config) : this(config.CellSize, config.Gap)
    {
    }

    public double Pitch => CellSize + Gap;

    public PixelRect ToPixels(GridRect r) =>
        new(Gap + r.Column * Pitch, Gap + r.Row * Pitch, r.ColumnSpan * Pitch - Gap, r.RowSpan * Pitch - Gap);

    /// <summary>Number of whole cells fitting in <paramref name="pixels"/>.</summary>
    public int FitCount(double pixels) => Math.Max(0, (int)Math.Floor((pixels - Gap) / Pitch));

    /// <summary>Pixels needed to show <paramref name="cells"/> cells including outer gaps.</summary>
    public double Extent(int cells) => Gap + cells * Pitch;

    /// <summary>Origin moved by a pointer delta, snapped to the nearest cell, kept non-negative.</summary>
    public GridRect Move(GridRect origin, double dx, double dy) => origin with
    {
        Column = Math.Max(0, origin.Column + Snap(dx)),
        Row = Math.Max(0, origin.Row + Snap(dy)),
    };

    /// <summary>Origin resized from its bottom-right corner by a pointer delta, snapped, at least the minimum size.</summary>
    public GridRect Resize(GridRect origin, double dx, double dy, GridSize min) => origin with
    {
        ColumnSpan = Math.Max(min.Columns, origin.ColumnSpan + Snap(dx)),
        RowSpan = Math.Max(min.Rows, origin.RowSpan + Snap(dy)),
    };

    private int Snap(double delta) => (int)Math.Round(delta / Pitch, MidpointRounding.AwayFromZero);
}

public readonly record struct GridSize(int Columns, int Rows);

public static class BoardRules
{
    /// <summary>
    /// True if <paramref name="candidate"/> lies within [0, maxColumns) × [0, maxRows) and overlaps no widget
    /// other than <paramref name="moving"/>.
    /// </summary>
    public static bool CanPlace(IEnumerable<WidgetConfig> widgets, WidgetConfig? moving, GridRect candidate,
        int maxColumns = int.MaxValue, int maxRows = int.MaxValue)
    {
        if (candidate.Column < 0 || candidate.Row < 0 || candidate.ColumnSpan < 1 || candidate.RowSpan < 1)
            return false;
        if (candidate.Right > maxColumns || candidate.Bottom > maxRows)
            return false;
        foreach (var w in widgets)
        {
            if (!ReferenceEquals(w, moving) && w.Bounds.Intersects(candidate))
                return false;
        }
        return true;
    }

    /// <summary>First free spot of <paramref name="size"/> in row-major order within the given bounds, or null.</summary>
    public static GridRect? FindFreeSpot(IReadOnlyCollection<WidgetConfig> widgets, GridSize size, int maxColumns, int maxRows)
    {
        for (var row = 0; row + size.Rows <= maxRows; row++)
        {
            for (var column = 0; column + size.Columns <= maxColumns; column++)
            {
                var candidate = new GridRect(column, row, size.Columns, size.Rows);
                if (CanPlace(widgets, null, candidate))
                    return candidate;
            }
        }
        return null;
    }

    /// <summary>Cells needed to show every widget.</summary>
    public static GridSize Extent(IEnumerable<WidgetConfig> widgets)
    {
        int columns = 0, rows = 0;
        foreach (var w in widgets)
        {
            columns = Math.Max(columns, w.Bounds.Right);
            rows = Math.Max(rows, w.Bounds.Bottom);
        }
        return new GridSize(columns, rows);
    }
}
