using NockMon.Core.Metrics;

namespace NockMon.Core.Agents;

/// <summary>Validation error reported to the agent with its message (the agent can correct its call).</summary>
public sealed class AgentException(string message) : ModelContextProtocol.McpException(message);

/// <summary>
/// Units agents receive: CPU in logical CPUs (1.0 = one logical processor busy), sizes in MB, rates per second, all
/// rounded to 4 significant digits (token budget; the sampled data is not more precise than that).
/// </summary>
public static class AgentUnits
{
    private const double MB = 1024.0 * 1024.0;

    public static string Label(MetricUnit unit) => unit switch
    {
        MetricUnit.Cpu => "logical CPUs",
        MetricUnit.Bytes => "MB",
        MetricUnit.BytesPerSecond => "MB/s",
        MetricUnit.PerSecond => "/s",
        MetricUnit.Count => "count",
        MetricUnit.Milliseconds => "ms",
        MetricUnit.Megahertz => "MHz",
        MetricUnit.Percent => "%",
        MetricUnit.Celsius => "°C",
        MetricUnit.Watts => "W",
        MetricUnit.Rpm => "rpm",
        MetricUnit.Timestamp => "time",
        _ => "",
    };

    /// <summary>Converts a stored value (bytes, logical CPUs…) to the agent unit of <see cref="Label"/>, rounded.</summary>
    public static double Convert(MetricUnit unit, double value) => Round(unit switch
    {
        MetricUnit.Bytes or MetricUnit.BytesPerSecond => value / MB,
        _ => value,
    });

    public static double Mb(double bytes) => Round(bytes / MB);

    /// <summary>4 significant digits, at most 6 decimals; NaN and infinities become 0 (JSON has no NaN).</summary>
    public static double Round(double value)
    {
        if (!double.IsFinite(value) || value == 0)
            return 0;
        var decimals = 3 - (int)Math.Floor(Math.Log10(Math.Abs(value)));
        return Math.Round(value, Math.Clamp(decimals, 0, 6));
    }

    /// <summary>Local wall-clock time, second resolution: what the user reads on their clock.</summary>
    public static string Time(DateTime utc) => utc.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public static string DateTime(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
}
