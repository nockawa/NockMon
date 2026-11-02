namespace NockMon.Sdk;

/// <summary>Unit of a plugin metric: how NockMon formats, scales and labels it.</summary>
public enum PluginMetricUnit
{
    /// <summary>A plain number.</summary>
    Count,
    /// <summary>0..100.</summary>
    Percent,
    Celsius,
    Rpm,
    Volts,
    Amperes,
    Watts,
    Megahertz,
    Milliseconds,
    Bytes,
    BytesPerSecond,
    /// <summary>Events per second.</summary>
    PerSecond,
}

/// <summary>A metric a plugin publishes.</summary>
/// <param name="Id">Stable identifier within the source (stored in layouts: never change it for the same quantity).</param>
/// <param name="Name">Full name ("CPU Package temperature"), for pickers and chart legends.</param>
/// <param name="ShortName">Name within its group ("Package").</param>
/// <param name="Group">What it belongs to ("AMD Ryzen 9 7950X"): pickers group by it.</param>
public sealed record PluginMetric(string Id, string Name, string ShortName, PluginMetricUnit Unit, string Group);

/// <summary>
/// A source of metrics offered by a plugin (API 1.1). Public, non-abstract, with a parameterless constructor: the host
/// finds it in the plugin assembly, creates one instance at start, calls <see cref="Start"/>, and disposes it at exit.
/// Its metrics become NockMon metrics: recorded in the history at every sample, usable in charts, value widgets and
/// value lists, and readable by AI agents.
/// <para>
/// Threading: <see cref="Metrics"/> and <see cref="TryRead"/> are called on NockMon's sampling thread at every sample
/// (0.5 s by default). They must return at once: read the hardware (or the network…) on your own thread and keep the
/// latest values for them.
/// </para>
/// </summary>
public interface IMetricPlugin : IDisposable
{
    /// <summary>Stable identifier, unique within the plugin (stored in layouts with each metric).</summary>
    string Id { get; }

    /// <summary>Name shown in pickers.</summary>
    string DisplayName { get; }

    /// <summary>Called once, on the UI thread, before the first read: start producing values (in the background).</summary>
    void Start(IPluginContext context);

    /// <summary>
    /// The metrics offered now; empty until known. Return the same instance until the list changes (then a new
    /// one): NockMon compares references.
    /// </summary>
    IReadOnlyList<PluginMetric> Metrics { get; }

    /// <summary>
    /// Writes the latest value of each metric of <paramref name="metrics"/> (an instance <see cref="Metrics"/>
    /// returned), in its order; <see cref="double.NaN"/> when unknown (NockMon then holds the previous value).
    /// </summary>
    /// <returns>False if <paramref name="metrics"/> is no longer the current list (nothing is recorded this time).</returns>
    bool TryRead(IReadOnlyList<PluginMetric> metrics, Span<double> values);
}

/// <summary>What the host gives a metric source.</summary>
public interface IPluginContext
{
    /// <summary>Folder for the plugin's own files (cache, state), shared with its widgets. Created on first access.</summary>
    string DataDirectory { get; }

    /// <summary>Version of the running NockMon.</summary>
    Version HostVersion { get; }
}
