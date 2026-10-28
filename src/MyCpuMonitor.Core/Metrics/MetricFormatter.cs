using System.Globalization;

namespace MyCpuMonitor.Core.Metrics;

public static class MetricFormatter
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Converts a CPU value in cores to a percentage for the given scale.</summary>
    public static double CpuPercent(double cores, CpuScale scale, int logicalProcessors) => scale switch
    {
        CpuScale.PerCore => cores * 100.0,
        _ => logicalProcessors > 0 ? cores * 100.0 / logicalProcessors : 0,
    };

    public static string Format(double value, MetricUnit unit, CpuScale scale, int logicalProcessors)
    {
        if (double.IsNaN(value))
            return "";
        var c = CultureInfo.CurrentCulture;
        return unit switch
        {
            MetricUnit.Cpu => CpuPercent(value, scale, logicalProcessors).ToString("0.0", c) + " %",
            MetricUnit.Bytes => FormatBytes(value),
            MetricUnit.BytesPerSecond => FormatBytes(value) + "/s",
            MetricUnit.PerSecond => value.ToString(value < 10 && value != 0 ? "0.0" : "#,0", c) + "/s",
            MetricUnit.Count => value.ToString("#,0", c),
            _ => value.ToString(c),
        };
    }

    /// <summary>Binary units, 3 significant digits: 999 B, 1.50 KB, 12.3 MB, 456 GB.</summary>
    public static string FormatBytes(double bytes)
    {
        var c = CultureInfo.CurrentCulture;
        var unit = 0;
        var v = Math.Abs(bytes);
        while (v >= 1000 && unit < ByteUnits.Length - 1)
        {
            v /= 1024;
            unit++;
        }
        if (bytes < 0)
            v = -v;
        if (unit == 0)
            return v.ToString("0", c) + " B";
        var format = Math.Abs(v) < 10 ? "0.00" : Math.Abs(v) < 100 ? "0.0" : "0";
        return v.ToString(format, c) + " " + ByteUnits[unit];
    }
}
