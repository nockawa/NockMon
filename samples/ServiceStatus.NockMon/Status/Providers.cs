using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace ServiceStatus.NockMon.Status;

/// <summary>
/// Reads one protocol of status page. Stateless and offline: the poller fetches <see cref="Requests"/> and hands the
/// bodies, in that order, to <see cref="Parse"/>, which throws <see cref="FormatException"/> on a body it cannot read.
/// </summary>
public interface IStatusProvider
{
    IReadOnlyList<string> Requests(CatalogEntry entry);

    ServiceReading Parse(CatalogEntry entry, IReadOnlyList<string> bodies);
}

public static partial class Providers
{
    // Bounds on what one page may bring: the largest real ones (Cloudflare, 481 components) fit well within.
    internal const int MaxComponents = 1000;
    internal const int MaxIncidents = 50;
    internal const int MaxMaintenances = 20;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32 };

    public static IStatusProvider For(ProviderKind kind) => kind switch
    {
        ProviderKind.Statuspage => StatuspageProvider.Instance,
        ProviderKind.IncidentIo => IncidentIoProvider.Instance,
        ProviderKind.Slack => SlackProvider.Instance,
        ProviderKind.Google => GoogleProvider.Instance,
        ProviderKind.Aws => AwsProvider.Instance,
        ProviderKind.AzureRss => AzureRssProvider.Instance,
        ProviderKind.StatusIo => StatusIoProvider.Instance,
        ProviderKind.Heroku => HerokuProvider.Instance,
        ProviderKind.AzureDevOps => AzureDevOpsProvider.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static JsonDocument ParseJson(string body)
    {
        try
        {
            return JsonDocument.Parse(body, JsonOptions);
        }
        catch (JsonException e)
        {
            throw new FormatException("Not JSON.", e);
        }
    }

    internal static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    internal static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    internal static DateTimeOffset? Date(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    internal static DateTimeOffset? UnixSeconds(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s is > 0 and < 32_503_680_000
            ? DateTimeOffset.FromUnixTimeSeconds(s)
            : null;

    /// <summary>Statuspage's component statuses; incident.io uses the same words, with "full_outage".</summary>
    internal static Severity ComponentSeverity(string? status) => status switch
    {
        "operational" => Severity.Operational,
        "under_maintenance" => Severity.Maintenance,
        "degraded_performance" => Severity.Degraded,
        "partial_outage" => Severity.PartialOutage,
        "major_outage" or "full_outage" => Severity.MajorOutage,
        _ => Severity.Degraded, // A word this code doesn't know: something is off.
    };

    /// <summary>Statuspage's page indicator.</summary>
    internal static Severity IndicatorSeverity(string? indicator) => indicator switch
    {
        "none" or null => Severity.Operational,
        "maintenance" => Severity.Maintenance,
        "minor" => Severity.Degraded,
        "major" => Severity.PartialOutage,
        "critical" => Severity.MajorOutage,
        _ => Severity.Degraded,
    };

    /// <summary>An open incident is at least degraded, whatever impact it declares (often "none" while investigating).</summary>
    internal static Severity AtLeastDegraded(Severity severity) => severity < Severity.Degraded ? Severity.Degraded : severity;

    internal static Severity Max(Severity a, Severity b) => a > b ? a : b;

    /// <summary>
    /// A link from a fetched page, or null: https only, and only to the page's own hosts or Statuspage's shortlinks. NockMon
    /// runs as administrator: no scheme or host the catalog didn't name.
    /// </summary>
    internal static string? Link(string? url, CatalogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(entry.PageUrl, UriKind.Absolute, out var page))
            return null;
        if (!Uri.TryCreate(page, url, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        var host = uri.IdnHost;
        var allowed = new[] { page.IdnHost, Uri.TryCreate(entry.Url, UriKind.Absolute, out var api) ? api.IdnHost : null, "stspg.io" };
        return allowed.Any(h => h is not null && (host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>Text from a page, for a TextBlock: tags, entities and Markdown marks removed, blank runs collapsed, clipped.</summary>
    internal static string? Plain(string? text, int max = 600)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (text.Length > 20_000)
            text = text[..20_000];
        text = BlockTags().Replace(text, " ");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = MarkdownMarks().Replace(text, "");
        text = Blanks().Replace(text, " ").Trim();
        if (text.Length == 0)
            return null;
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    [GeneratedRegex(@"</?(p|br|div|li|ul|ol|h[1-6]|tr|td|th|table|blockquote|pre)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"(^|\s)#{1,6}\s|\*\*|__|`")]
    private static partial Regex MarkdownMarks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Blanks();
}

/// <summary>Atlassian Statuspage, and the pages serving its summary (incident.io's compatibility endpoint: no incidents).</summary>
internal sealed class StatuspageProvider : IStatusProvider
{
    public static readonly StatuspageProvider Instance = new();

    public IReadOnlyList<string> Requests(CatalogEntry entry) => [entry.Url.TrimEnd('/') + "/api/v2/summary.json"];

    public ServiceReading Parse(CatalogEntry entry, IReadOnlyList<string> bodies)
    {
        using var doc = Providers.ParseJson(bodies[0]);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object)
            throw new FormatException("Not a Statuspage summary.");
        var groups = Groups(root);
        var components = Components(root, groups);

        var incidents = new List<IncidentReading>();
        foreach (var i in Providers.Array(root, "incidents"))
        {
            if (Providers.Str(i, "status") is "resolved" or "postmortem" or "completed" || incidents.Count == Providers.MaxIncidents)
                continue;
            incidents.Add(new IncidentReading(
                Providers.Plain(Providers.Str(i, "name"), 200) ?? "Incident",
                Providers.Str(i, "status") ?? "",
                Providers.AtLeastDegraded(ImpactSeverity(Providers.Str(i, "impact"))),
                Providers.Date(Providers.Str(i, "started_at") ?? Providers.Str(i, "created_at")),
                Providers.Date(Providers.Str(i, "updated_at")),
                LatestUpdate(i),
                Providers.Link(Providers.Str(i, "shortlink"), entry),
                Affected(i, groups),
                []));
        }

        var maintenances = new List<MaintenanceReading>();
        foreach (var m in Providers.Array(root, "scheduled_maintenances"))
        {
            var state = Providers.Str(m, "status");
            if (state is "completed" or "resolved" || maintenances.Count == Providers.MaxMaintenances)
                continue;
            maintenances.Add(new MaintenanceReading(
                Providers.Plain(Providers.Str(m, "name"), 200) ?? "Maintenance",
                state is "in_progress" or "verifying",
                Providers.Date(Providers.Str(m, "scheduled_for")),
                Providers.Date(Providers.Str(m, "scheduled_until")),
                Providers.Link(Providers.Str(m, "shortlink"), entry),
                Affected(m, groups)));
        }

        return new ServiceReading(
            Providers.IndicatorSeverity(Providers.Str(status, "indicator")),
            Providers.Plain(Providers.Str(status, "description"), 200),
            components, incidents, maintenances, ListsComponents: true);
    }

    /// <summary>Group id → name.</summary>
    internal static Dictionary<string, string> Groups(JsonElement root)
    {
        var groups = new Dictionary<string, string>();
        foreach (var c in Providers.Array(root, "components"))
            if (IsGroup(c) && Providers.Str(c, "id") is { } id && Providers.Str(c, "name") is { } name)
                groups[id] = name;
        return groups;
    }

    /// <summary>
    /// The page's components, group headers excepted (they repeat their members). Hidden ones ("showcase": false, like
    /// GitHub's "Visit www.githubstatus.com") are skipped when the page shows others; some pages (Cloudflare) hide all.
    /// </summary>
    internal static List<ComponentReading> Components(JsonElement root, Dictionary<string, string> groups)
    {
        var list = new List<ComponentReading>();
        var showcases = Providers.Array(root, "components").Any(c => !IsGroup(c) && c.TryGetProperty("showcase", out var s) && s.ValueKind == JsonValueKind.True);
        foreach (var c in Providers.Array(root, "components"))
        {
            if (IsGroup(c) || showcases && c.TryGetProperty("showcase", out var s) && s.ValueKind == JsonValueKind.False)
                continue;
            if (list.Count == Providers.MaxComponents)
                break;
            list.Add(Component(c, groups));
        }
        return list;
    }

    private static bool IsGroup(JsonElement c) => c.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.True;

    private static ComponentReading Component(JsonElement c, Dictionary<string, string> groups) => new(
        Providers.Plain(Providers.Str(c, "name"), 200) ?? "?",
        Providers.Str(c, "group_id") is { } gid && groups.TryGetValue(gid, out var group) ? group : null,
        Providers.ComponentSeverity(Providers.Str(c, "status")));

    private static List<ComponentReading> Affected(JsonElement item, Dictionary<string, string> groups) =>
        Providers.Array(item, "components").Take(Providers.MaxComponents).Select(c => Component(c, groups)).ToList();

    private static string? LatestUpdate(JsonElement incident)
    {
        foreach (var u in Providers.Array(incident, "incident_updates")) // Newest first.
            return Providers.Plain(Providers.Str(u, "body"));
        return null;
    }

    private static Severity ImpactSeverity(string? impact) => impact switch
    {
        "maintenance" => Severity.Maintenance,
        "major" => Severity.PartialOutage,
        "critical" => Severity.MajorOutage,
        _ => Severity.Degraded,
    };
}

/// <summary>
/// incident.io: the components from its Statuspage-compatible summary, the incidents and maintenances from its widget
/// API (/api/v1/summary, which the page owner may disable: then use <see cref="StatuspageProvider"/>).
/// </summary>
internal sealed class IncidentIoProvider : IStatusProvider
{
    public static readonly IncidentIoProvider Instance = new();

    public IReadOnlyList<string> Requests(CatalogEntry entry)
    {
        var page = entry.Url.TrimEnd('/');
        return [page + "/api/v2/summary.json", page + "/api/v1/summary"];
    }

    public ServiceReading Parse(CatalogEntry entry, IReadOnlyList<string> bodies)
    {
        using var compat = Providers.ParseJson(bodies[0]);
        using var widget = Providers.ParseJson(bodies[1]);
        var root = compat.RootElement;
        var api = widget.RootElement;
        if (root.ValueKind != JsonValueKind.Object || api.ValueKind != JsonValueKind.Object || !api.TryGetProperty("ongoing_incidents", out _))
            throw new FormatException("Not an incident.io page.");
        var status = root.TryGetProperty("status", out var s) ? s : default;
        var components = StatuspageProvider.Components(root, StatuspageProvider.Groups(root));

        var incidents = Providers.Array(api, "ongoing_incidents").Take(Providers.MaxIncidents).Select(i => new IncidentReading(
            Providers.Plain(Providers.Str(i, "name"), 200) ?? "Incident",
            Providers.Str(i, "status") ?? "",
            Providers.AtLeastDegraded(Providers.ComponentSeverity(Providers.Str(i, "current_worst_impact"))),
            null,
            Providers.Date(Providers.Str(i, "last_update_at")),
            Providers.Plain(Providers.Str(i, "last_update_message")),
            Providers.Link(Providers.Str(i, "url"), entry),
            Affected(i),
            [])).ToList();

        var maintenances = Providers.Array(api, "in_progress_maintenances").Select(m => (m, true))
            .Concat(Providers.Array(api, "scheduled_maintenances").Select(m => (m, false)))
            .Take(Providers.MaxMaintenances)
            .Select(x => new MaintenanceReading(
                Providers.Plain(Providers.Str(x.m, "name"), 200) ?? "Maintenance",
                x.Item2,
                Providers.Date(Providers.Str(x.m, x.Item2 ? "started_at" : "starts_at")),
                Providers.Date(Providers.Str(x.m, x.Item2 ? "scheduled_end_at" : "ends_at")),
                Providers.Link(Providers.Str(x.m, "url"), entry),
                Affected(x.m)))
            .ToList();

        return new ServiceReading(
            Providers.IndicatorSeverity(Providers.Str(status, "indicator")),
            Providers.Plain(Providers.Str(status, "description"), 200),
            components, incidents, maintenances, ListsComponents: true);
    }

    private static List<ComponentReading> Affected(JsonElement item) =>
        Providers.Array(item, "affected_components").Take(Providers.MaxComponents).Select(c => new ComponentReading(
            Providers.Plain(Providers.Str(c, "name"), 200) ?? "?",
            Providers.Plain(Providers.Str(c, "group_name"), 200),
            Providers.ComponentSeverity(Providers.Str(c, "current_status")))).ToList();
}

/// <summary>Slack's own API: the open incidents (type incident, notice or outage) and planned maintenance.</summary>
internal sealed class SlackProvider : IStatusProvider
{
    public static readonly SlackProvider Instance = new();

    public IReadOnlyList<string> Requests(CatalogEntry entry) => [entry.Url];

    public ServiceReading Parse(CatalogEntry entry, IReadOnlyList<string> bodies)
    {
        using var doc = Providers.ParseJson(bodies[0]);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Providers.Str(root, "status") is null)
            throw new FormatException("Not Slack's status.");
        var incidents = new List<IncidentReading>();
        var maintenances = new List<MaintenanceReading>();
        foreach (var i in Providers.Array(root, "active_incidents"))
        {
            var state = Providers.Str(i, "status");
            var name = Providers.Plain(Providers.Str(i, "title"), 200) ?? "Incident";
            var url = Providers.Link(Providers.Str(i, "url"), entry);
            if (state == "scheduled")
            {
                if (maintenances.Count < Providers.MaxMaintenances)
                    maintenances.Add(new MaintenanceReading(name, false, null, null, url, Services(i, Severity.Maintenance)));
                continue;
            }
            if (state is not "active" || incidents.Count == Providers.MaxIncidents)
                continue;
            var severity = Providers.Str(i, "type") switch
            {
                "outage" => Severity.MajorOutage,
                "incident" => Severity.PartialOutage,
                _ => Severity.Degraded, // "notice"
            };
            // Notes in no documented order: the newest by date.
            var note = Providers.Array(i, "notes").Select(n => (At: Providers.Date(Providers.Str(n, "date_created")), Body: Providers.Str(n, "body")))
                .OrderByDescending(n => n.At).FirstOrDefault();
            incidents.Add(new IncidentReading(
                name, state, severity,
                Providers.Date(Providers.Str(i, "date_created")),
                Providers.Date(Providers.Str(i, "date_updated")),
                Providers.Plain(note.Body), url, Services(i, severity), []));
        }
        return new ServiceReading(Severity.Operational, null, [], incidents, maintenances, ListsComponents: false);
    }

    private static List<ComponentReading> Services(JsonElement incident, Severity severity) =>
        Providers.Array(incident, "services").Select(s => s.ValueKind == JsonValueKind.String ? s.GetString() : null)
            .OfType<string>().Take(Providers.MaxComponents).Select(s => new ComponentReading(s, null, severity)).ToList();
}

/// <summary>Google Cloud and Google Workspace: the incidents feed (open = no end), products and locations.</summary>
internal sealed class GoogleProvider : IStatusProvider
{
    public static readonly GoogleProvider Instance = new();

    public IReadOnlyList<string> Requests(CatalogEntry entry) => [entry.Url];

    public ServiceReading Parse(CatalogEntry entry, IReadOnlyList<string> bodies)
    {
        using var doc = Providers.ParseJson(bodies[0]);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new FormatException("Not a Google incidents feed.");
        var incidents = new List<IncidentReading>();
        foreach (var i in root.EnumerateArray())
        {
            if (i.ValueKind != JsonValueKind.Object || Providers.Str(i, "end") is not null || incidents.Count == Providers.MaxIncidents)
                continue;
            var severity = Providers.Str(i, "status_impact") switch
            {
                "SERVICE_OUTAGE" => Severity.MajorOutage,
                "SERVICE_DISRUPTION" => Severity.PartialOutage,
                _ => Severity.Degraded, // SERVICE_INFORMATION
            };
            var update = i.TryGetProperty("most_recent_update", out var u) ? u : default;
            var locations = Providers.Array(i, "currently_affected_locations")
                .SelectMany(l => new[] { Providers.Str(l, "id"), Providers.Str(l, "title") }).OfType<string>().Distinct().ToList();
            incidents.Add(new IncidentReading(
                Providers.Plain(Providers.Str(i, "external_desc"), 200) ?? "Incident",
                "ongoing", severity,
                Providers.Date(Providers.Str(i, "begin")),
                Providers.Date(Providers.Str(i, "modified")),
                Providers.Plain(Providers.Str(update, "text")),
                Providers.Link(Providers.Str(i, "uri"), entry),
                Providers.Array(i, "affected_products").Select(p => Providers.Str(p, "title")).OfType<string>()
                    .Take(Providers.MaxComponents).Select(p => new ComponentReading(p, null, severity)).ToList(),
                locations));
        }
        return new ServiceReading(Severity.Operational, null, [], incidents, [], ListsComponents: false);
    }
}

/// <summary>
/// AWS Health's public current events (unofficial, the feed behind health.aws.amazon.com): one event per service and
/// region, status 1 to 3. Served in UTF-16: the poller decodes by charset.
/// </summary>
internal sealed class AwsProvider : IStatusProvider
{
    public static readonly AwsProvider Instance = new();

    public IReadOnlyList<string> Requests(CatalogEntry entry) => [entry.Url];

    public ServiceReading Parse(CatalogEntry entry, IReadOnlyList<string> bodies)
    {
        using var doc = Providers.ParseJson(bodies[0]);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new FormatException("Not the AWS current events.");
        var incidents = new List<IncidentReading>();
        foreach (var e in root.EnumerateArray())
        {
            var severity = Level(Providers.Str(e, "status"));
            if (e.ValueKind != JsonValueKind.Object || severity == Severity.Operational || incidents.Count == Providers.MaxIncidents)
                continue;
            // arn:aws:health:<region>::event/...
            var region = Providers.Str(e, "arn")?.Split(':') is { Length: > 3 } arn && arn[3].Length > 0 ? arn[3] : null;
            var regionName = Providers.Str(e, "region_name");
            var locations = new[] { region, regionName }.OfType<string>().Distinct().ToList();
            var log = Providers.Array(e, "event_log").Select(l => (At: Providers.UnixSeconds(Providers.Str(l, "timestamp")), Message: Providers.Str(l, "message")))
                .OrderByDescending(l => l.At).FirstOrDefault();
            var services = e.TryGetProperty("impacted_services", out var impacted) && impacted.ValueKind == JsonValueKind.Object
                ? impacted.EnumerateObject().Take(Providers.MaxComponents)
                    .Select(p => new ComponentReading(Providers.Str(p.Value, "service_name") ?? p.Name, region, Level(Providers.Str(p.Value, "current"))))
                    .Where(c => c.Severity > Severity.Operational).ToList()
                : [];
            if (services.Count == 0 && Providers.Str(e, "service_name") is { } service)
                services.Add(new ComponentReading(service, region, severity));
            var summary = Providers.Plain(Providers.Str(e, "summary"), 200) ?? "Event";
            incidents.Add(new IncidentReading(
                regionName is null ? summary : $"{summary} ({regionName})",
                "ongoing", severity,
                Providers.UnixSeconds(Providers.Str(e, "date")),
                log.At,
                Providers.Plain(log.Message),
                null, services, locations));
        }
        return new ServiceReading(Severity.Operational, null, [], incidents, [], ListsComponents: false);
    }

    private static Severity Level(string? status) => status switch
    {
        "1" => Severity.Degraded,
        "2" => Severity.PartialOutage,
        "3" => Severity.MajorOutage,
        _ => Severity.Operational, // "0": resolved.
    };
}

/// <summary>Azure's RSS feed: an item per widespread incident, empty otherwise. No severity in the feed: partial outage.</summary>
internal sealed class AzureRssProvider : IStatusProvider
{
    public static readonly AzureRssProvider Instance = new();

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 4_000_000,
        IgnoreComments = true,
    };

    public IReadOnlyList<string> Requests(CatalogEntry entry) => [entry.Url];

    public ServiceReading Parse(CatalogEntry entry, IReadOnlyList<string> bodies)
    {
        var incidents = new List<IncidentReading>();
        var sawChannel = false;
        try
        {
            using var reader = XmlReader.Create(new StringReader(bodies[0]), Settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                if (reader.LocalName == "channel")
                    sawChannel = true;
                else if (reader.LocalName == "item" && incidents.Count < Providers.MaxIncidents)
                    incidents.Add(Item(reader.ReadSubtree(), entry));
            }
        }
        catch (XmlException e)
        {
            throw new FormatException("Not an RSS feed.", e);
        }
        if (!sawChannel)
            throw new FormatException("Not an RSS feed.");
        return new ServiceReading(Severity.Operational, null, [], incidents, [], ListsComponents: false);
    }

    private static IncidentReading Item(XmlReader item, CatalogEntry entry)
    {
        string? title = null, link = null, description = null, date = null;
        using (item)
        {
            item.Read(); // <item>
            item.Read();
            while (!item.EOF)
            {
                // Reading an element's content moves to the next node: no Read() after it, or an adjacent element is skipped.
                if (item.NodeType != XmlNodeType.Element || item.Depth != 1)
                {
                    item.Read();
                    continue;
                }
                switch (item.LocalName)
                {
                    case "title": title = item.ReadElementContentAsString(); break;
                    case "link": link = item.ReadElementContentAsString(); break;
                    case "description": description = item.ReadElementContentAsString(); break;
                    case "pubDate": date = item.ReadElementContentAsString(); break;
                    default: item.Skip(); break;
                }
            }
        }
        // RFC 822 dates, Azure writing UTC as "Z" ("Tue, 03 Nov 2026 07:24:00 Z"), which .NET doesn't read.
        if (date is not null && date.TrimEnd().EndsWith(" Z", StringComparison.Ordinal))
            date = date.TrimEnd()[..^2] + " +00:00";
        var at = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTimeOffset?)null;
        return new IncidentReading(Providers.Plain(title, 200) ?? "Incident", "ongoing", Severity.PartialOutage, at, at,
            Providers.Plain(description), Providers.Link(link, entry), [], []);
    }
}
