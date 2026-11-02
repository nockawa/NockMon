namespace NockMon.Core.Metrics;

/// <summary>A metric published by an external source (a plugin's metric source): an id stable across sessions, names, a unit.</summary>
/// <param name="Group">What it belongs to, for pickers ("AMD Ryzen 9 7950X", "Nuvoton NCT6799D").</param>
public sealed record ExternalMetric(string Id, string Name, string ShortName, MetricUnit Unit, string Group);

/// <summary>
/// A source of metrics outside NockMon (a plugin's, through the SDK). The engine reads it at every sample, on its
/// sampling thread, and records the values in the history like its own (see <see cref="PluginSeries"/>).
/// </summary>
public interface IExternalMetricSource
{
    /// <summary>The plugin's package id.</summary>
    string Plugin { get; }

    /// <summary>The source's id within the plugin.</summary>
    string Source { get; }

    string DisplayName { get; }

    /// <summary>The metrics offered now: the same instance until they change. Sampling thread.</summary>
    IReadOnlyList<ExternalMetric> Metrics { get; }

    /// <summary>
    /// Latest values of <paramref name="metrics"/> (an instance <see cref="Metrics"/> returned), NaN when unknown.
    /// Sampling thread, every sample: copies cached values, never blocks.
    /// </summary>
    /// <returns>False when <paramref name="metrics"/> is no longer current (nothing written).</returns>
    bool TryRead(IReadOnlyList<ExternalMetric> metrics, Span<double> values);
}

/// <summary>Plugin and source ids, compared ignoring case.</summary>
public sealed class SourceKeyComparer : IEqualityComparer<(string Plugin, string Source)>
{
    public static SourceKeyComparer Instance { get; } = new();

    public bool Equals((string Plugin, string Source) a, (string Plugin, string Source) b) =>
        string.Equals(a.Plugin, b.Plugin, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string Plugin, string Source) key) =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Plugin), StringComparer.OrdinalIgnoreCase.GetHashCode(key.Source));
}

/// <summary>One external source as registered: its metrics, by id.</summary>
public sealed record ExternalSourceInfo(string Plugin, string Source, string DisplayName, IReadOnlyList<ExternalMetric> Metrics);

/// <summary>
/// The external metrics known this session: names and units for <see cref="PluginSeries"/>, pickers and agents.
/// Registered by the engine when a source's metrics change; read from any thread.
/// </summary>
public static class ExternalMetricCatalog
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<(string, string), (ExternalSourceInfo Info, Dictionary<string, ExternalMetric> ById)> Entries =
        new(SourceKeyComparer.Instance);
    private static IReadOnlyList<ExternalSourceInfo> _sources = [];

    /// <summary>Every registered source, in registration order.</summary>
    public static IReadOnlyList<ExternalSourceInfo> Sources => _sources;

    public static void Register(IExternalMetricSource source, IReadOnlyList<ExternalMetric> metrics)
    {
        var info = new ExternalSourceInfo(source.Plugin, source.Source, source.DisplayName, metrics);
        var byId = new Dictionary<string, ExternalMetric>(StringComparer.Ordinal);
        foreach (var metric in metrics)
            byId.TryAdd(metric.Id, metric);
        lock (Gate)
        {
            var known = Entries.ContainsKey((source.Plugin, source.Source));
            Entries[(source.Plugin, source.Source)] = (info, byId);
            _sources = known
                ? _sources.Select(s => SourceKeyComparer.Instance.Equals((s.Plugin, s.Source), (info.Plugin, info.Source)) ? info : s).ToArray()
                : [.. _sources, info];
        }
    }

    public static ExternalMetric? Find(string plugin, string source, string metric)
    {
        lock (Gate)
            return Entries.TryGetValue((plugin, source), out var entry) ? entry.ById.GetValueOrDefault(metric) : null;
    }

    /// <summary>Tests only.</summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
            _sources = [];
        }
    }
}

/// <summary>One source's values at a sample, as the engine read them (<see cref="Valid"/>: false when the read failed).</summary>
public readonly record struct ExternalReading(IExternalMetricSource Source, IReadOnlyList<ExternalMetric> Metrics, double[] Values, bool Valid);
