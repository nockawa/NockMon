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

    /// <summary>Share of samples that make the bulk of a chart: the rest can be spikes.</summary>
    public const double BulkPercentile = 0.97;

    /// <summary>The maximum must exceed the bulk by this factor before the spikes above it are clipped.</summary>
    public const double SpikeRatio = 3;

    /// <summary>
    /// Axis ceiling for <paramref name="values"/> (a chart's top edge, per sample) such that a few spikes don't flatten the
    /// rest: when the maximum exceeds <see cref="SpikeRatio"/> × the <see cref="BulkPercentile"/> percentile, the ceiling fits
    /// that percentile (20 % headroom, then rounded up) and the spikes go above it. A zero bulk (rare events, where the spikes
    /// are the signal) is never clipped. <paramref name="scratch"/> (at least as long as the values) receives a sorted copy.
    /// </summary>
    public static double SpikeAwareCeiling(ReadOnlySpan<float> values, Span<float> scratch, MetricUnit unit)
    {
        if (values.IsEmpty)
            return 1;
        var sorted = scratch[..values.Length];
        values.CopyTo(sorted);
        sorted.Sort();
        double max = sorted[^1], bulk = sorted[(int)(BulkPercentile * (sorted.Length - 1))];
        return bulk > 0 && max > SpikeRatio * bulk ? NiceCeiling(bulk * 1.2, unit) : NiceCeiling(max, unit);
    }

    private static double Decimal125(double value)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }
}
