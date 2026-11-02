using System.Globalization;
using System.Runtime.CompilerServices;

namespace NockMon.Core.Metrics;

public static class MetricFormatter
{
    private static readonly string[] ByteUnits = [" B", " KB", " MB", " GB", " TB"];

    /// <summary>Converts logical CPUs used to a percentage of the machine.</summary>
    public static double CpuPercent(double cpus, int logicalProcessors) =>
        logicalProcessors > 0 ? cpus * 100.0 / logicalProcessors : 0;

    public static string Format(double value, MetricUnit unit, CpuScale scale, int logicalProcessors)
    {
        if (double.IsNaN(value))
            return "";
        return unit switch
        {
            MetricUnit.Cpu when scale == CpuScale.Cpus => With(value, Math.Abs(value) < 10 ? "0.00" : "0.0", " CPUs"),
            MetricUnit.Cpu => With(CpuPercent(value, logicalProcessors), "0.0", " %"),
            MetricUnit.Bytes => FormatBytes(value),
            MetricUnit.BytesPerSecond => FormatBytes(value, "/s"),
            MetricUnit.PerSecond => With(value, value < 10 && value != 0 ? "0.0" : "#,0", "/s"),
            MetricUnit.Count => value.ToString("#,0", CultureInfo.CurrentCulture),
            MetricUnit.Milliseconds => With(value, value < 10 ? "0.###" : "#,0.#", " ms"),
            MetricUnit.Megahertz => With(value, "#,0", " MHz"),
            MetricUnit.Percent => With(value, value < 10 ? "0.0" : "0", " %"),
            MetricUnit.Celsius => With(value, "0", " °C"),
            MetricUnit.Watts => With(value, value < 100 ? "0.0" : "0", " W"),
            MetricUnit.Rpm => With(value, "#,0", " rpm"),
            _ => value.ToString(CultureInfo.CurrentCulture),
        };
    }

    /// <summary>The value and its unit in one string (every cell, every sample: one allocation, not two).</summary>
    private static string With(double value, string format, string unit, string suffix = "")
    {
        var text = new DefaultInterpolatedStringHandler(0, 1, CultureInfo.CurrentCulture, stackalloc char[64]);
        text.AppendFormatted(value, format);
        text.AppendLiteral(unit);
        text.AppendLiteral(suffix);
        return text.ToStringAndClear();
    }

    /// <summary>Binary units, 3 significant digits: 999 B, 1.50 KB, 12.3 MB, 456 GB.</summary>
    public static string FormatBytes(double bytes) => FormatBytes(bytes, "");

    private static string FormatBytes(double bytes, string suffix)
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
            return With(v, "0", " B", suffix);
        var format = Math.Abs(v) < 10 ? "0.00" : Math.Abs(v) < 100 ? "0.0" : "0";
        return With(v, format, ByteUnits[unit], suffix);
    }
}
