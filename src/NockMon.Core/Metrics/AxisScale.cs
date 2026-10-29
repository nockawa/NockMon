namespace NockMon.Core.Metrics;

public static class AxisScale
{
    /// <summary>
    /// Smallest "nice" value ≥ <paramref name="value"/>: 1, 2, 5 × 10^k, or for byte units 1, 2, 5, 10, 20, 50, 100, 200, 500 × 1024^k
    /// so the axis label reads as a round number once formatted. Returns 1 for non-positive input.
    /// </summary>
    public static double NiceCeiling(double value, MetricUnit unit)
    {
        if (!(value > 0) || double.IsInfinity(value))
            return 1;

        if (unit is MetricUnit.Bytes or MetricUnit.BytesPerSecond)
        {
            var scale = 1.0;
            while (value / scale >= 1000)
                scale *= 1024;
            return Decimal125(value / scale) * scale;
        }
        return Decimal125(value);
    }

    private static double Decimal125(double value)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }
}
