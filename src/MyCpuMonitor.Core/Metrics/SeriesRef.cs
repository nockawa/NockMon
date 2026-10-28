using System.Text.Json.Serialization;

namespace MyCpuMonitor.Core.Metrics;

/// <summary>A time series the history can produce: metric × target.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SystemSeries), "system")]
[JsonDerivedType(typeof(CoreSeries), "core")]
[JsonDerivedType(typeof(ProcessSeries), "process")]
[JsonDerivedType(typeof(ObjectTypeSeries), "objectType")]
[JsonDerivedType(typeof(GpuSeries), "gpu")]
public abstract record SeriesRef
{
    public abstract MetricInfo Info { get; }

    /// <summary>Label used when the user gave none.</summary>
    public abstract string DefaultLabel { get; }
}

public sealed record SystemSeries(SystemMetric Metric) : SeriesRef
{
    public override MetricInfo Info => MetricCatalog.Get(Metric);
    public override string DefaultLabel => Info.DisplayName;
}

public sealed record CoreSeries(CoreMetric Metric, int Core) : SeriesRef
{
    public override MetricInfo Info => MetricCatalog.Get(Metric);
    public override string DefaultLabel => $"Core {Core} {Info.ShortName}";
}

/// <summary>
/// A GPU metric of an adapter (by display name). For <see cref="Gpu.GpuMetric.Engine"/>, <paramref name="Engine"/> is an
/// engine name pattern ("Compute*", "3D", "Video*") and the busiest matching engine is reported.
/// </summary>
public sealed record GpuSeries(Gpu.GpuMetric Metric, string Adapter, string? Engine = null) : SeriesRef
{
    public override MetricInfo Info => MetricCatalog.Get(Metric);

    public override string DefaultLabel => Metric == Gpu.GpuMetric.Engine
        ? $"{ShortAdapter} {Engine}"
        : $"{ShortAdapter} {Info.ShortName}";

    /// <summary>"NVIDIA GeForce RTX 4070 Ti" → "RTX 4070 Ti".</summary>
    private string ShortAdapter => Adapter
        .Replace("NVIDIA GeForce ", "", StringComparison.OrdinalIgnoreCase)
        .Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase)
        .Replace("AMD Radeon(TM) ", "Radeon ", StringComparison.OrdinalIgnoreCase)
        .Replace("Intel(R) ", "", StringComparison.OrdinalIgnoreCase);
}

public enum ObjectTypeMetric
{
    Objects,
    Handles,
}

/// <summary>Live objects or handles of a kernel object type ("Token", "File", "Job"…), at scan resolution.</summary>
public sealed record ObjectTypeSeries(string TypeName, ObjectTypeMetric Metric) : SeriesRef
{
    public override MetricInfo Info => new($"{TypeName} {(Metric == ObjectTypeMetric.Objects ? "objects" : "handles")}",
        Metric == ObjectTypeMetric.Objects ? "Objects" : "Handles", MetricUnit.Count, true);

    public override string DefaultLabel => Info.DisplayName;
}

/// <summary>Sum of <paramref name="Field"/> over all processes whose image name matches <paramref name="Pattern"/> (see <see cref="Model.NamePattern"/>).</summary>
public sealed record ProcessSeries(ProcessField Field, string Pattern) : SeriesRef
{
    public override MetricInfo Info => MetricCatalog.Get(Field);
    public override string DefaultLabel => $"{Pattern} {Info.ShortName}";
}
