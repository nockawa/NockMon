using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeUsage.NockMon;

/// <summary>How far from fully functional Claude is: the badge's color, yellow to red.</summary>
public enum StatusSeverity { Operational, Minor, Major, Critical }

/// <summary>A component that is not operational ("degraded_performance", "partial_outage", "major_outage", "under_maintenance").</summary>
public sealed record StatusComponent(string Name, string Status, StatusSeverity Severity);

/// <summary>An unresolved incident; <see cref="LatestUpdate"/> is the text of its newest update.</summary>
public sealed record StatusIncident(string Name, string Status, StatusSeverity Severity, DateTimeOffset? StartedAt, string? LatestUpdate, string? Url);

/// <summary>A reading of the status page: the worst of the page's indicator, its components and its active incidents.</summary>
public sealed record ClaudeStatus(
    StatusSeverity Severity,
    string Description,
    IReadOnlyList<StatusComponent> Affected,
    IReadOnlyList<StatusIncident> Incidents,
    DateTimeOffset CheckedAt);

/// <summary>
/// Claude's service status, shared by every widget: one poller (every 2 minutes, while a widget wants it) on the public
/// Statuspage summary of status.claude.com, as claude-usage does. Conditional requests (ETag): an unchanged page costs a
/// 304. A failed check keeps the previous reading; one older than <see cref="StaleAfter"/> is no longer shown.
/// </summary>
public static class StatusService
{
    public const string PageUrl = "https://status.claude.com";
    private const string SummaryUrl = PageUrl + "/api/v2/summary.json";
    private const int MaxText = 600;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    private static readonly Lock Gate = new();
    // Public endpoint: no credentials. The page is a few KB; anything far bigger is not it.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1 << 20 };
    private static int _subscribers;
    private static CancellationTokenSource? _loop;
    private static string? _etag;
    private static volatile ClaudeStatus? _current;

    /// <summary>The latest reading, null until the first one. Replaced as a whole: compare references to detect a change.</summary>
    public static ClaudeStatus? Current => _current;

    /// <summary>Polls (if not yet); dispose to unsubscribe.</summary>
    public static IDisposable Subscribe()
    {
        lock (Gate)
        {
            if (_subscribers++ == 0)
            {
                _loop = new CancellationTokenSource();
                var token = _loop.Token;
                _ = Task.Run(() => LoopAsync(token));
            }
        }
        return new Subscription();
    }

    private static async Task LoopAsync(CancellationToken cancel)
    {
        // A loop restarted (the last widget re-created on a page switch or an edit) resumes the schedule.
        if (_current is { } last && DateTimeOffset.UtcNow - last.CheckedAt < Interval)
        {
            if (!await DelayAsync(Interval - (DateTimeOffset.UtcNow - last.CheckedAt), cancel))
                return;
        }
        while (!cancel.IsCancellationRequested)
        {
            var ok = await CheckAsync(cancel);
            if (!await DelayAsync(ok ? Interval : RetryDelay, cancel))
                return;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(delay, cancel);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> CheckAsync(CancellationToken cancel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SummaryUrl);
            if (_etag is { } etag && _current is not null && EntityTagHeaderValue.TryParse(etag, out var tag))
                request.Headers.IfNoneMatch.Add(tag);
            using var response = await Http.SendAsync(request, cancel);
            if (response.StatusCode == HttpStatusCode.NotModified && _current is { } unchanged)
            {
                _current = unchanged with { CheckedAt = DateTimeOffset.UtcNow };
                return true;
            }
            if (!response.IsSuccessStatusCode)
                return false;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            _current = Parse(doc.RootElement, DateTimeOffset.UtcNow);
            _etag = response.Headers.ETag?.ToString();
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
                                      or KeyNotFoundException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Statuspage's v2 summary (page, status, components, incidents).</summary>
    public static ClaudeStatus Parse(JsonElement root, DateTimeOffset checkedAt)
    {
        var status = root.GetProperty("status");
        var severity = IndicatorSeverity(Text(status, "indicator"));
        var description = Text(status, "description") ?? "";

        var affected = new List<StatusComponent>();
        if (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
        {
            // Group headers repeat their members' status. Hidden components are not services, unless the page hides them
            // all (some Statuspage pages do).
            var showcases = components.EnumerateArray().Any(c => !IsGroup(c) && c.TryGetProperty("showcase", out var s) && s.ValueKind == JsonValueKind.True);
            foreach (var c in components.EnumerateArray())
            {
                if (IsGroup(c) || showcases && c.TryGetProperty("showcase", out var s) && s.ValueKind == JsonValueKind.False)
                    continue;
                var componentStatus = Text(c, "status") ?? "operational";
                var componentSeverity = ComponentSeverity(componentStatus);
                if (componentSeverity == StatusSeverity.Operational)
                    continue;
                affected.Add(new StatusComponent(Clip(Text(c, "name") ?? "?"), componentStatus, componentSeverity));
                severity = Max(severity, componentSeverity);
            }
        }

        var incidents = new List<StatusIncident>();
        if (root.TryGetProperty("incidents", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in list.EnumerateArray())
            {
                var incidentStatus = Text(i, "status") ?? "";
                if (incidentStatus is "resolved" or "postmortem")
                    continue;
                // An open incident, even of no declared impact, means the service is not quite all right.
                var impact = Max(IndicatorSeverity(Text(i, "impact")), StatusSeverity.Minor);
                string? update = null;
                if (i.TryGetProperty("incident_updates", out var updates) && updates.ValueKind == JsonValueKind.Array)
                    foreach (var u in updates.EnumerateArray()) // Newest first.
                    {
                        update = Text(u, "body");
                        break;
                    }
                var url = Text(i, "shortlink");
                incidents.Add(new StatusIncident(
                    Clip(Text(i, "name") ?? "Incident"),
                    incidentStatus,
                    impact,
                    DateTimeOffset.TryParse(Text(i, "started_at") ?? Text(i, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null,
                    update is null ? null : Clip(update),
                    Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null));
                severity = Max(severity, impact);
            }
        }

        affected.Sort((a, b) => b.Severity.CompareTo(a.Severity));
        return new ClaudeStatus(severity, Clip(description), affected, incidents, checkedAt);
    }

    /// <summary>Plain words for a component status.</summary>
    public static string Describe(string componentStatus) => componentStatus switch
    {
        "degraded_performance" => "Degraded performance",
        "partial_outage" => "Partial outage",
        "major_outage" => "Major outage",
        "under_maintenance" => "Under maintenance",
        _ => componentStatus.Replace('_', ' '),
    };

    private static StatusSeverity IndicatorSeverity(string? indicator) => indicator switch
    {
        "minor" or "maintenance" => StatusSeverity.Minor,
        "major" => StatusSeverity.Major,
        "critical" => StatusSeverity.Critical,
        _ => StatusSeverity.Operational,
    };

    private static StatusSeverity ComponentSeverity(string status) => status switch
    {
        "operational" => StatusSeverity.Operational,
        "partial_outage" => StatusSeverity.Major,
        "major_outage" => StatusSeverity.Critical,
        _ => StatusSeverity.Minor, // Degraded, under maintenance, or a status this code doesn't know.
    };

    private static StatusSeverity Max(StatusSeverity a, StatusSeverity b) => a > b ? a : b;

    private static bool IsGroup(JsonElement c) => c.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Clip(string text) => text.Length <= MaxText ? text : text[..MaxText] + "…";

    private sealed class Subscription : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (Gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                if (--_subscribers > 0)
                    return;
                _loop?.Cancel();
                _loop = null;
            }
        }
    }
}
