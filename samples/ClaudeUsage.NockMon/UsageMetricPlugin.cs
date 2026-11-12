using NockMon.Sdk;

namespace ClaudeUsage.NockMon;

/// <summary>
/// The weekly usage as NockMon metrics, for the whole session: NockMon keeps their history, charts and value widgets can
/// show them, AI agents can read them (MCP). Polls through <see cref="UsageService"/> every 10 minutes at most (a widget on
/// the board may poll faster); missing until the first reading.
/// </summary>
public sealed class UsageMetricPlugin : IMetricPlugin
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(7);
    private const string Group = "Claude usage";

    private static readonly PluginMetric[] All =
    [
        new("weekly.used", "Claude weekly used", "Used", PluginMetricUnit.Percent, Group),
        new("weekly.elapsed", "Claude weekly elapsed", "Elapsed", PluginMetricUnit.Percent, Group),
        new("weekly.hoursToReset", "Claude weekly hours to reset", "Hours to reset", PluginMetricUnit.Count, Group),
        new("weekly.pacePerDay", "Claude weekly pace (%/day, 24 h)", "Pace %/day", PluginMetricUnit.Count, Group),
        new("weekly.burnPerHour", "Claude weekly burn (%/h, 3 h)", "Burn %/h", PluginMetricUnit.Count, Group),
        new("weekly.targetPerHour", "Claude weekly target burn (%/h)", "Target %/h", PluginMetricUnit.Count, Group),
    ];

    private IDisposable? _subscription;

    public string Id => "usage";

    public string DisplayName => "Claude usage";

    public void Start(IPluginContext context) => _subscription = UsageService.Subscribe(context.DataDirectory, TimeSpan.FromMinutes(10));

    public IReadOnlyList<PluginMetric> Metrics => All;

    public bool TryRead(IReadOnlyList<PluginMetric> metrics, Span<double> values)
    {
        if (!ReferenceEquals(metrics, All))
            return false;
        values.Fill(double.NaN);
        var state = UsageService.State;
        if (state.Weekly is not { } weekly)
            return true;
        var left = weekly.ResetsAt - DateTimeOffset.UtcNow;
        values[0] = weekly.UtilPct;
        values[1] = Math.Clamp((Window - left).TotalSeconds / Window.TotalSeconds * 100, 0, 100);
        values[2] = Math.Max(0, left.TotalHours);
        if (state.Pace is { } pace)
            values[3] = pace.PctPerDay;
        if (state.Trend is { } trend)
        {
            values[4] = trend.PctPerHour;
            values[5] = trend.TargetPctPerHour;
        }
        return true;
    }

    public void Dispose() => _subscription?.Dispose();
}
