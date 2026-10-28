using System.Text.Json.Serialization;

namespace MyCpuMonitor.Core.Metrics;

/// <summary>A time series the history can produce: metric × target.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SystemSeries), "system")]
[JsonDerivedType(typeof(CoreSeries), "core")]
[JsonDerivedType(typeof(ProcessSeries), "process")]
public abstract record SeriesRef
{
    public abstract MetricInfo Info { get; }
}

public sealed record SystemSeries(SystemMetric Metric) : SeriesRef
{
    public override MetricInfo Info => MetricCatalog.Get(Metric);
}

public sealed record CoreSeries(CoreMetric Metric, int Core) : SeriesRef
{
    public override MetricInfo Info => MetricCatalog.Get(Metric);
}

/// <summary>Sum of <paramref name="Field"/> over all processes whose image name matches <paramref name="Pattern"/> (see <see cref="Model.NamePattern"/>).</summary>
public sealed record ProcessSeries(ProcessField Field, string Pattern) : SeriesRef
{
    public override MetricInfo Info => MetricCatalog.Get(Field);
}
