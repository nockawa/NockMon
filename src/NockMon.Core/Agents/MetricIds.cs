using NockMon.Core.Gpu;
using NockMon.Core.Help;
using NockMon.Core.Metrics;
using NockMon.Core.Sampling;

namespace NockMon.Core.Agents;

/// <summary>One metric as agents see it: a stable id ("scope.Name") and what its target is.</summary>
/// <param name="Target">What the target parameter holds; null when the metric takes none.</param>
/// <param name="History">True if <c>get_metric_history</c> can chart it (otherwise current values only).</param>
public sealed record MetricEntry(string Id, string Name, string Unit, string? Target, bool History, string Summary);

/// <summary>
/// Agent-facing metric ids: "system.Cpu", "core.Usage", "process.WorkingSet", "gpu.Temperature", "objectType.Handles",
/// plus current-value-only scopes "driver.*" and "poolTag.*" (kernel tables). Case-insensitive.
/// </summary>
public static class MetricIds
{
    public const string ProcessTarget = "image name pattern: '*' and '?' wildcards, alternatives separated by ';' (\"chrome\" matches chrome.exe)";
    public const string CoreTarget = "logical processor index (0-based)";
    public const string GpuTarget = "GPU adapter name (see get_overview); optional with a single GPU";
    public const string ObjectTypeTarget = "kernel object type name (\"File\", \"Token\", \"Event\"…)";

    /// <summary>Every id, in catalog order. GPU engines are excluded: they are only polled for the widgets showing them.</summary>
    public static IEnumerable<MetricEntry> All()
    {
        foreach (var m in Enum.GetValues<SystemMetric>())
            yield return Entry($"system.{m}", MetricCatalog.Get(m), null, Docs.Of(m));
        foreach (var m in Enum.GetValues<CoreMetric>())
            yield return Entry($"core.{m}", MetricCatalog.Get(m), CoreTarget, Docs.Of(m));
        foreach (var f in Enum.GetValues<ProcessField>())
            yield return Entry($"process.{f}", MetricCatalog.Get(f), f == ProcessField.Name ? null : ProcessTarget, Docs.Of(f));
        foreach (var m in Enum.GetValues<GpuMetric>().Where(m => m != GpuMetric.Engine))
            yield return Entry($"gpu.{m}", MetricCatalog.Get(m), GpuTarget, Docs.Of(m));
        foreach (var m in Enum.GetValues<ObjectTypeMetric>())
            yield return Entry($"objectType.{m}", new MetricInfo($"Kernel objects: {m.ToString().ToLowerInvariant()}", m.ToString(), MetricUnit.Count, true),
                ObjectTypeTarget, Docs.Of(m));
        foreach (var f in Enum.GetValues<DriverField>().Where(f => f != DriverField.Name))
            yield return new MetricEntry($"driver.{f}", Docs.Of(f).Title, f.ToString().EndsWith("Rate", StringComparison.Ordinal) ? "/s" : "ms",
                null, false, Docs.Of(f).Summary);
        foreach (var f in Enum.GetValues<PoolTagField>().Where(f => f != PoolTagField.Tag))
            yield return new MetricEntry($"poolTag.{f}", Docs.Of(f).Title, f is PoolTagField.AllocationRate ? "/s" : f is PoolTagField.Outstanding ? "count"
                : f.ToString().EndsWith("Growth", StringComparison.Ordinal) ? "KB/s" : "MB", null, false, Docs.Of(f).Summary);
    }

    private static MetricEntry Entry(string id, MetricInfo info, string? target, Doc doc) =>
        new(id, info.DisplayName, AgentUnits.Label(info.Unit), target, info.HasHistory, doc.Summary);

    /// <summary>Full documentation of a metric id (title, summary, details in light markdown).</summary>
    public static Doc Describe(string id)
    {
        var (scope, name) = Split(id);
        return scope switch
        {
            "system" => Docs.Of(Parse<SystemMetric>(id, name)),
            "core" => Docs.Of(Parse<CoreMetric>(id, name)),
            "process" => Docs.Of(Parse<ProcessField>(id, name)),
            "gpu" => Docs.Of(Parse<GpuMetric>(id, name)),
            "objecttype" => Docs.Of(Parse<ObjectTypeMetric>(id, name)),
            "driver" => Docs.Of(Parse<DriverField>(id, name)),
            "pooltag" => Docs.Of(Parse<PoolTagField>(id, name)),
            _ => throw Unknown(id),
        };
    }

    /// <summary>The history series of <paramref name="id"/> applied to <paramref name="target"/>.</summary>
    /// <param name="gpuAdapters">Known adapter names (resolves an omitted or partial GPU target).</param>
    public static SeriesRef ToSeries(string id, string? target, IReadOnlyList<string> gpuAdapters, int logicalProcessors)
    {
        var (scope, name) = Split(id);
        switch (scope)
        {
            case "system":
                return new SystemSeries(Parse<SystemMetric>(id, name));
            case "core":
                var metric = Parse<CoreMetric>(id, name);
                if (!int.TryParse(target, out var core) || core < 0 || core >= logicalProcessors)
                    throw new AgentException($"{id} needs a target: {CoreTarget}, 0 to {logicalProcessors - 1}.");
                return new CoreSeries(metric, core);
            case "process":
                var field = Parse<ProcessField>(id, name);
                if (!MetricCatalog.Get(field).HasHistory)
                    throw new AgentException($"{id} has no history (current values only: use get_top_processes or get_process). " +
                        $"With history: {string.Join(", ", MetricCatalog.ProcessHistoryFields.Select(f => $"process.{f}"))}.");
                if (string.IsNullOrWhiteSpace(target))
                    throw new AgentException($"{id} needs a target: {ProcessTarget}.");
                return new ProcessSeries(field, target.Trim());
            case "gpu":
                var gpuMetric = Parse<GpuMetric>(id, name);
                if (gpuMetric == GpuMetric.Engine)
                    throw Unknown(id);
                return new GpuSeries(gpuMetric, ResolveAdapter(id, target, gpuAdapters));
            case "objecttype":
                var objects = Parse<ObjectTypeMetric>(id, name);
                if (string.IsNullOrWhiteSpace(target))
                    throw new AgentException($"{id} needs a target: {ObjectTypeTarget}.");
                return new ObjectTypeSeries(target.Trim(), objects);
            case "driver" or "pooltag":
                throw new AgentException($"{id} has no history: use get_kernel_activity for current values.");
            default:
                throw Unknown(id);
        }
    }

    private static string ResolveAdapter(string id, string? target, IReadOnlyList<string> adapters)
    {
        if (adapters.Count == 0)
            throw new AgentException("No GPU adapter was detected.");
        if (string.IsNullOrWhiteSpace(target))
            return adapters.Count == 1 ? adapters[0] : throw new AgentException($"{id} needs a target, one of: {string.Join(", ", adapters)}.");
        return adapters.FirstOrDefault(a => a.Equals(target, StringComparison.OrdinalIgnoreCase))
               ?? adapters.FirstOrDefault(a => a.Contains(target.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new AgentException($"Unknown GPU '{target}', known: {string.Join(", ", adapters)}.");
    }

    private static (string Scope, string Name) Split(string id)
    {
        var dot = id.IndexOf('.');
        if (dot <= 0 || dot == id.Length - 1)
            throw Unknown(id);
        return (id[..dot].Trim().ToLowerInvariant(), id[(dot + 1)..].Trim());
    }

    private static T Parse<T>(string id, string name) where T : struct, Enum =>
        Enum.TryParse<T>(name, ignoreCase: true, out var value) && Enum.IsDefined(value) && !int.TryParse(name, out _)
            ? value
            : throw Unknown(id);

    private static AgentException Unknown(string id) => new($"Unknown metric '{id}'. list_metrics gives the valid ids (\"system.Cpu\", \"process.WorkingSet\"…).");
}
