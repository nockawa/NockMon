using System.Text.Json;

namespace ServiceStatus.NockMon.Status;

/// <summary>
/// status.io (GitLab): https://status.io/1.0/status/{page id}. Status codes: 100 operational, 200 planned maintenance,
/// 300 degraded performance, 400 partial disruption, 500 service disruption, 600 security event.
/// </summary>
internal sealed class StatusIoProvider : IStatusProvider
{
    public static readonly StatusIoProvider Instance = new();

    public IReadOnlyList<string> Requests(CatalogEntry entry) => [entry.Url];

    public ServiceReading Parse(CatalogEntry entry, IReadOnlyList<string> bodies)
    {
        using var doc = Providers.ParseJson(bodies[0]);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("status_overall", out var overall))
            throw new FormatException("Not a status.io status.");
        var pageId = Uri.TryCreate(entry.Url, UriKind.Absolute, out var api) ? api.Segments[^1].Trim('/') : "";

        var components = Providers.Array(result, "status").Take(Providers.MaxComponents)
            .Select(c => new ComponentReading(Providers.Plain(Providers.Str(c, "name"), 200) ?? "?", null, Code(Providers.Str(c, "status_code"))))
            .ToList();

        var incidents = new List<IncidentReading>();
        foreach (var i in Providers.Array(result, "incidents"))
        {
            if (i.TryGetProperty("current_active", out var active) && active.ValueKind == JsonValueKind.False || incidents.Count == Providers.MaxIncidents)
                continue;
            var latest = Providers.Array(i, "messages").Select(m => (At: Providers.Date(Providers.Str(m, "datetime")), Message: m))
                .OrderByDescending(m => m.At).FirstOrDefault();
            var severity = Providers.AtLeastDegraded(latest.Message.ValueKind == JsonValueKind.Object ? Code(Providers.Str(latest.Message, "status")) : Severity.Degraded);
            incidents.Add(new IncidentReading(
                Providers.Plain(Providers.Str(i, "name"), 200) ?? "Incident",
                Providers.Str(latest.Message, "state") switch { "100" => "investigating", "200" => "identified", "300" => "monitoring", _ => "ongoing" },
                severity,
                Providers.Date(Providers.Str(i, "datetime_open")),
                latest.At,
                Providers.Plain(Providers.Str(latest.Message, "details")),
                Providers.Str(i, "_id") is { } id && pageId.Length > 0 ? Providers.Link($"https://status.io/pages/incident/{pageId}/{id}", entry) : null,
                Named(i, "components_affected", severity),
                Providers.Array(i, "containers_affected").Select(c => Providers.Str(c, "name")).OfType<string>().ToList()));
        }

        var maintenances = new List<MaintenanceReading>();
        if (result.TryGetProperty("maintenance", out var maintenance))
        {
            var all = Providers.Array(maintenance, "active").Select(m => (Item: m, InProgress: true))
                .Concat(Providers.Array(maintenance, "upcoming").Select(m => (Item: m, InProgress: false)));
            foreach (var (m, inProgress) in all.Take(Providers.MaxMaintenances))
            {
                maintenances.Add(new MaintenanceReading(
                    Providers.Plain(Providers.Str(m, "name"), 200) ?? "Maintenance",
                    inProgress,
                    Providers.Date(Providers.Str(m, "datetime_planned_start")),
                    Providers.Date(Providers.Str(m, "datetime_planned_end")),
                    Providers.Str(m, "_id") is { } id && pageId.Length > 0 ? Providers.Link($"https://status.io/pages/maintenance/{pageId}/{id}", entry) : null,
                    Named(m, "components_affected", Severity.Maintenance)));
            }
        }

        return new ServiceReading(Code(Providers.Str(overall, "status_code")), Providers.Plain(Providers.Str(overall, "status"), 200),
            components, incidents, maintenances, ListsComponents: true);
    }

    private static List<ComponentReading> Named(JsonElement item, string name, Severity severity) =>
        Providers.Array(item, name).Select(c => Providers.Str(c, "name")).OfType<string>().Take(Providers.MaxComponents)
            .Select(n => new ComponentReading(n, null, severity)).ToList();

    private static Severity Code(string? code) => code switch
    {
        "100" => Severity.Operational,
        "200" => Severity.Maintenance,
        "300" => Severity.Degraded,
        "400" or "600" => Severity.PartialOutage,
        "500" => Severity.MajorOutage,
        _ => Severity.Degraded,
    };
}

/// <summary>Heroku's API v4: its systems (Apps, Data, Tools) green, yellow or red, the open incidents, scheduled work.</summary>
internal sealed class HerokuProvider : IStatusProvider
{
    public static readonly HerokuProvider Instance = new();

    public IReadOnlyList<string> Requests(CatalogEntry entry) => [entry.Url];

    public ServiceReading Parse(CatalogEntry entry, IReadOnlyList<string> bodies)
    {
        using var doc = Providers.ParseJson(bodies[0]);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var systems) || systems.ValueKind != JsonValueKind.Array)
            throw new FormatException("Not Heroku's status.");
        var components = systems.EnumerateArray().Take(Providers.MaxComponents)
            .Select(s => new ComponentReading(Providers.Str(s, "system") ?? "?", null, Color(Providers.Str(s, "status")))).ToList();

        var incidents = new List<IncidentReading>();
        foreach (var i in Providers.Array(root, "incidents"))
        {
            if (Over(i) || incidents.Count == Providers.MaxIncidents)
                continue;
            var affected = Systems(i);
            var severity = Providers.AtLeastDegraded(affected.Count > 0 ? affected.Max(c => c.Severity) : Severity.Degraded);
            var update = Newest(i);
            incidents.Add(new IncidentReading(
                Providers.Plain(Providers.Str(i, "title"), 200) ?? "Incident",
                Providers.Str(update, "update_type") ?? Providers.Str(i, "state") ?? "",
                severity,
                Providers.Date(Providers.Str(i, "created_at")),
                Providers.Date(Providers.Str(i, "updated_at")),
                Providers.Plain(Providers.Str(update, "contents")),
                Providers.Link(Providers.Str(i, "full_url"), entry),
                affected, []));
        }

        var maintenances = new List<MaintenanceReading>();
        foreach (var m in Providers.Array(root, "scheduled"))
        {
            // In progress only: Heroku leaves finished maintenance listed as "upcoming", unresolved, its end sometimes
            // posted as one more "scheduled" update, and gives no machine-readable times to tell upcoming from past.
            if (Over(m) || Providers.Str(m, "state") is not ("in_progress" or "active") || maintenances.Count == Providers.MaxMaintenances)
                continue;
            maintenances.Add(new MaintenanceReading(
                Providers.Plain(Providers.Str(m, "title"), 200) ?? "Maintenance",
                true,
                null, null,
                Providers.Link(Providers.Str(m, "full_url"), entry),
                Systems(m)));
        }
        return new ServiceReading(Severity.Operational, null, components, incidents, maintenances, ListsComponents: true);
    }

    private static bool Over(JsonElement item) =>
        item.TryGetProperty("resolved", out var r) && r.ValueKind == JsonValueKind.True
        || Providers.Str(item, "resolved_at") is not null
        || Providers.Array(item, "updates").Any(u => Providers.Str(u, "update_type") is "resolved" or "completed");

    private static JsonElement Newest(JsonElement item) =>
        Providers.Array(item, "updates").OrderByDescending(u => Providers.Date(Providers.Str(u, "created_at"))).FirstOrDefault();

    private static List<ComponentReading> Systems(JsonElement item) =>
        Providers.Array(item, "systems").Take(Providers.MaxComponents)
            .Select(s => new ComponentReading(Providers.Str(s, "name") ?? "?", null, Color(Providers.Str(s, "status")))).ToList();

    private static Severity Color(string? status) => status switch
    {
        "green" => Severity.Operational,
        "blue" => Severity.Maintenance,
        "yellow" => Severity.Degraded,
        "red" => Severity.MajorOutage,
        _ => Severity.Degraded,
    };
}

/// <summary>
/// Azure DevOps' health API: each service (Boards, Repos, Pipelines…) per geography (United States, Europe…), healthy,
/// advisory, degraded or unhealthy. No incident list: the overall message stands for one when no component explains it.
/// </summary>
internal sealed class AzureDevOpsProvider : IStatusProvider
{
    public static readonly AzureDevOpsProvider Instance = new();

    public IReadOnlyList<string> Requests(CatalogEntry entry) => [entry.Url];

    public ServiceReading Parse(CatalogEntry entry, IReadOnlyList<string> bodies)
    {
        using var doc = Providers.ParseJson(bodies[0]);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status) || !root.TryGetProperty("services", out _))
            throw new FormatException("Not Azure DevOps' health.");
        var components = new List<ComponentReading>();
        foreach (var s in Providers.Array(root, "services"))
            foreach (var g in Providers.Array(s, "geographies"))
                if (components.Count < Providers.MaxComponents)
                    components.Add(new ComponentReading(Providers.Str(s, "id") ?? "?", Providers.Str(g, "name") ?? Providers.Str(g, "id"), Health(Providers.Str(g, "health"))));

        var overall = Health(Providers.Str(status, "health"));
        var message = Providers.Plain(Providers.Str(status, "message"), 200);
        var incidents = new List<IncidentReading>();
        if (overall > Severity.Operational && !components.Any(c => c.Severity > Severity.Operational))
            incidents.Add(new IncidentReading(message ?? "Service disruption", "ongoing", Providers.AtLeastDegraded(overall),
                null, Providers.Date(Providers.Str(root, "lastUpdated")), null, null, [], []));
        return new ServiceReading(overall, message, components, incidents, [], ListsComponents: true);
    }

    private static Severity Health(string? health) => health switch
    {
        "healthy" => Severity.Operational,
        "advisory" or "degraded" => Severity.Degraded,
        "unhealthy" => Severity.MajorOutage,
        _ => Severity.Degraded,
    };
}
