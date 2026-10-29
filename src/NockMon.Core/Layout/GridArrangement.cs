namespace NockMon.Core.Layout;

/// <summary>Arranges <c>count</c> equal cells in a rectangle (small multiples).</summary>
public static class GridArrangement
{
    /// <summary>Preferred cell width / height: wide enough for a time series, tall enough to read it.</summary>
    public const double DefaultAspect = 1.6;

    /// <summary>
    /// Columns and rows for <paramref name="count"/> cells. With <paramref name="columns"/> &gt; 0 it is fixed; otherwise the
    /// column count whose cells are closest (log ratio) to <paramref name="targetAspect"/> wins, fewer empty cells breaking ties.
    /// </summary>
    public static (int Columns, int Rows) Arrange(int count, double width, double height, int columns = 0, double targetAspect = DefaultAspect)
    {
        if (count <= 0)
            return (0, 0);
        if (columns > 0)
        {
            columns = Math.Min(columns, count);
            return (columns, (count + columns - 1) / columns);
        }

        (int Columns, int Rows) best = (count, 1);
        var bestScore = double.MaxValue;
        var bestEmpty = int.MaxValue;
        for (var c = 1; c <= count; c++)
        {
            var r = (count + c - 1) / c;
            var aspect = width / c / (height / r);
            var score = width > 0 && height > 0 ? Math.Abs(Math.Log(aspect / targetAspect)) : c;
            var empty = c * r - count;
            if (score < bestScore - 1e-9 || (Math.Abs(score - bestScore) <= 1e-9 && empty < bestEmpty))
            {
                best = (c, r);
                bestScore = score;
                bestEmpty = empty;
            }
        }
        return best;
    }
}
