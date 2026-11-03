namespace ServiceStatus.NockMon.Status;

/// <summary>
/// How far from fully functional a service is, in rising order. <see cref="Unknown"/>: no recent reading (shown grey,
/// never as fine). <see cref="Maintenance"/> is planned work: shown, not counted as an issue.
/// </summary>
public enum Severity { Unknown, Operational, Maintenance, Degraded, PartialOutage, MajorOutage }

/// <summary>How a status page is read. Values are stored in the user catalog: never rename them.</summary>
public enum ProviderKind { Statuspage, IncidentIo, Slack, Google, Aws, AzureRss, StatusIo, Heroku, AzureDevOps }

/// <summary>A component (or an affected product) and its state. <see cref="Group"/>: its group or region, if any.</summary>
public sealed record ComponentReading(string Name, string? Group, Severity Severity);

/// <summary>
/// An open incident. <see cref="Components"/>: what it affects, when the page says so (empty: unknown, it counts for
/// every filter); <see cref="Locations"/>: its regions (AWS, Google), empty when not regional.
/// </summary>
public sealed record IncidentReading(
    string Name,
    string Status,
    Severity Severity,
    DateTimeOffset? StartedAt,
    DateTimeOffset? UpdatedAt,
    string? LatestUpdate,
    string? Url,
    IReadOnlyList<ComponentReading> Components,
    IReadOnlyList<string> Locations);

/// <summary>A maintenance window, in progress or scheduled.</summary>
public sealed record MaintenanceReading(
    string Name,
    bool InProgress,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? Url,
    IReadOnlyList<ComponentReading> Components);

/// <summary>
/// What a provider read from a page. <see cref="Indicator"/>: the page's own overall state (Statuspage, Slack), used only
/// when no filter applies. <see cref="ListsComponents"/>: the page lists all its components, not just the affected ones.
/// </summary>
public sealed record ServiceReading(
    Severity Indicator,
    string? Description,
    IReadOnlyList<ComponentReading> Components,
    IReadOnlyList<IncidentReading> Incidents,
    IReadOnlyList<MaintenanceReading> Maintenances,
    bool ListsComponents);

/// <summary>
/// Which components (or products) and regions of a page count. Patterns: '*' and '?' wildcards, case-insensitive,
/// matched on a component's name or its group (a group name selects its members). Empty lists select everything.
/// </summary>
public sealed record StatusFilter(IReadOnlyList<string> Include, IReadOnlyList<string> Exclude, IReadOnlyList<string> Regions)
{
    public static readonly StatusFilter All = new([], [], []);

    public bool IsAll => Include.Count == 0 && Exclude.Count == 0 && Regions.Count == 0;

    public bool Matches(ComponentReading component) =>
        (Include.Count == 0 || Include.Any(p => Glob.Matches(p, component.Name) || component.Group is { } g && Glob.Matches(p, g)))
        && !Exclude.Any(p => Glob.Matches(p, component.Name) || component.Group is { } g && Glob.Matches(p, g));

    /// <summary>Not regional, or in one of the regions.</summary>
    public bool MatchesRegions(IReadOnlyList<string> locations) =>
        Regions.Count == 0 || locations.Count == 0 || locations.Any(l => Regions.Any(p => Glob.Matches(p, l)));
}

/// <summary>A status page NockMon can follow: built in, or added by the user.</summary>
/// <param name="Id">Stable identifier, stored in the widgets' settings.</param>
/// <param name="Url">The page's base URL (Statuspage, incident.io) or the feed's URL (the others).</param>
/// <param name="PageUrl">The page people read, linked from the widget.</param>
public sealed record CatalogEntry(
    string Id,
    string Name,
    ProviderKind Kind,
    string Url,
    string PageUrl,
    StatusFilter DefaultFilter,
    bool Custom = false)
{
    /// <summary>Regions mean something for this page (the settings offer a region filter).</summary>
    public bool Regional => Kind is ProviderKind.Aws or ProviderKind.Google;
}

/// <summary>'*' (any run) and '?' (one character) wildcards, case-insensitive.</summary>
public static class Glob
{
    public static bool Matches(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    /// <summary>"a; b, c" → [a, b, c].</summary>
    public static IReadOnlyList<string> Split(string? patterns) =>
        string.IsNullOrWhiteSpace(patterns) ? [] : patterns.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
