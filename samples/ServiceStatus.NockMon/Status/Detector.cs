using System.Text.Json;
using System.Text.RegularExpressions;

namespace ServiceStatus.NockMon.Status;

/// <summary>A fetched body: the URL after redirects, the content type, the text.</summary>
public sealed record Fetched(Uri Url, string? ContentType, string Body);

/// <summary>
/// Recognizes a status page from its URL, for "Add by URL": Statuspage (a summary with page and status), incident.io (the
/// same summary without incidents, and its widget API), then status.io (the page's HTML names its status.io page id).
/// Each API answer must be JSON: some pages answer an unknown API path with their HTML and a 200.
/// </summary>
public static partial class Detector
{
    public static async Task<(CatalogEntry? Entry, string Message)> DetectAsync(
        string input, Func<Uri, CancellationToken, Task<Fetched?>> fetch, CancellationToken cancel)
    {
        var text = input.Trim();
        if (text.Length == 0)
            return (null, "Enter the status page's address.");
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0)
            return (null, "Only https status pages can be followed.");
        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var suffix in new[] { "/api/v2/summary.json", "/api/v2/status.json", "/api/v1/summary", "/history" })
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                path = path[..^suffix.Length];
        var origin = uri.GetLeftPart(UriPartial.Authority) + path;

        var summary = await JsonAsync(new Uri(origin + "/api/v2/summary.json"), fetch, cancel);
        if (summary is not { } s || s.Doc.RootElement is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("page", out var page) || !root.TryGetProperty("status", out _))
        {
            summary?.Doc.Dispose();
            return await StatusIoAsync(origin, fetch, cancel)
                   ?? (null, "Not a supported status page: only Atlassian Statuspage, incident.io and status.io pages can be added by URL.");
        }
        using (s.Doc)
        {
            // Redirects (status.anthropic.com → status.claude.com): follow the page where it lives now, if it is still a
            // page's summary (not some other API serving the same shape).
            const string summaryPath = "/api/v2/summary.json";
            if (!s.Url.AbsolutePath.EndsWith(summaryPath, StringComparison.OrdinalIgnoreCase) || s.Url.Scheme != Uri.UriSchemeHttps)
                return (null, $"This page redirects to {s.Url.Host}, which this plugin can't follow.");
            var final = s.Url.GetLeftPart(UriPartial.Authority) + s.Url.AbsolutePath[..^summaryPath.Length];
            var name = Providers.Str(page, "name") is { Length: > 0 } n ? n.Trim() : s.Url.Host;
            var pageUrl = Catalog.IsHttps(Providers.Str(page, "url")) ? Providers.Str(page, "url")! : final;
            var kind = ProviderKind.Statuspage;
            if (!root.TryGetProperty("incidents", out _))
            {
                // incident.io: its incidents come from the widget API, when the page owner enabled it.
                var widget = await JsonAsync(new Uri(final + "/api/v1/summary"), fetch, cancel);
                using (widget?.Doc)
                {
                    if (widget?.Doc.RootElement is { ValueKind: JsonValueKind.Object } w && w.TryGetProperty("ongoing_incidents", out _))
                        kind = ProviderKind.IncidentIo;
                }
            }
            var id = "custom:" + new Uri(final).Host.ToLowerInvariant() + new Uri(final).AbsolutePath.TrimEnd('/').ToLowerInvariant();
            return (new CatalogEntry(id, Providers.Plain(name, 80) ?? new Uri(final).Host, kind, final, pageUrl, StatusFilter.All, Custom: true),
                kind == ProviderKind.Statuspage && !root.TryGetProperty("incidents", out _)
                    ? "Found. This page publishes its components but not its incidents."
                    : "Found.");
        }
    }

    /// <summary>A status.io page: its HTML links its page id, whose status the public API serves.</summary>
    private static async Task<(CatalogEntry? Entry, string Message)?> StatusIoAsync(string origin, Func<Uri, CancellationToken, Task<Fetched?>> fetch, CancellationToken cancel)
    {
        var html = await fetch(new Uri(origin + "/"), cancel);
        if (html is null || html.Url.Scheme != Uri.UriSchemeHttps || StatusIoPage().Match(html.Body) is not { Success: true } match)
            return null;
        var api = "https://status.io/1.0/status/" + match.Groups[1].Value;
        var status = await JsonAsync(new Uri(api), fetch, cancel);
        using (status?.Doc)
        {
            if (status?.Doc.RootElement is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("status_overall", out _))
                return null;
        }
        var page = html.Url.GetLeftPart(UriPartial.Authority) + "/";
        var title = Title().Match(html.Body) is { Success: true } t ? Providers.Plain(t.Groups[1].Value, 80) : null;
        var id = "custom:" + html.Url.Host.ToLowerInvariant();
        return (new CatalogEntry(id, title ?? html.Url.Host, ProviderKind.StatusIo, api, page, StatusFilter.All, Custom: true), "Found.");
    }

    [GeneratedRegex(@"status\.io/pages/([0-9a-f]{24})", RegexOptions.IgnoreCase)]
    private static partial Regex StatusIoPage();

    [GeneratedRegex(@"<title[^>]*>([^<]{1,200})</title>", RegexOptions.IgnoreCase)]
    private static partial Regex Title();

    private static async Task<(Uri Url, JsonDocument Doc)?> JsonAsync(Uri url, Func<Uri, CancellationToken, Task<Fetched?>> fetch, CancellationToken cancel)
    {
        var fetched = await fetch(url, cancel);
        if (fetched is null || fetched.ContentType is not { } type || !type.Contains("json", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return (fetched.Url, Providers.ParseJson(fetched.Body));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
