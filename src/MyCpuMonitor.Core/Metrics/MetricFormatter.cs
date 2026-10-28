using System.Globalization;

namespace MyCpuMonitor.Core.Metrics;

public static class MetricFormatter
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Converts logical CPUs used to a percentage of the machine.</summary>
    public static double CpuPercent(double cpus, int logicalProcessors) =>
        logicalProcessors > 0 ? cpus * 100.0 / logicalProcessors : 0;

    public static string Format(double value, MetricUnit unit, CpuScale scale, int logicalProcessors)
    {
        if (double.IsNaN(value))
            return "";
        var c = CultureInfo.CurrentCulture;
        return unit switch
        {
            MetricUnit.Cpu when scale == CpuScale.Cpus => value.ToString(Math.Abs(value) < 10 ? "0.00" : "0.0", c) + " CPUs",
            MetricUnit.Cpu => CpuPercent(value, logicalProcessors).ToString("0.0", c) + " %",
            MetricUnit.Bytes => FormatBytes(value),
            MetricUnit.BytesPerSecond => FormatBytes(value) + "/s",
            MetricUnit.PerSecond => value.ToString(value < 10 && value != 0 ? "0.0" : "#,0", c) + "/s",
            MetricUnit.Count => value.ToString("#,0", c),
            MetricUnit.Milliseconds => value.ToString(value < 10 ? "0.###" : "#,0.#", c) + " ms",
            MetricUnit.Megahertz => value.ToString("#,0", c) + " MHz",
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
