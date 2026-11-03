namespace ServiceStatus.NockMon.Status;

/// <summary>
/// A followed service as a widget shows it: its severity under the widget's filter, and what explains it.
/// <see cref="Maintenances"/>: in progress, or starting within a day.
/// </summary>
public sealed record ServiceState(
    CatalogEntry Entry,
    Severity Severity,
    IReadOnlyList<ComponentReading> Affected,
    IReadOnlyList<IncidentReading> Incidents,
    IReadOnlyList<MaintenanceReading> Maintenances,
    DateTimeOffset? ReadAt,
    string? Error)
{
    /// <summary>Degraded or worse: what the widget counts. Maintenance and unknown are shown, not counted.</summary>
    public bool IsIssue => Severity >= Severity.Degraded;
}

public static class Evaluation
{
    private static readonly TimeSpan MaintenanceHorizon = TimeSpan.FromDays(1);

    /// <summary>
    /// The service's severity: the worst of its included components, its relevant open incidents (an incident that
    /// names no component counts) and maintenances in progress, and the page's own indicator when nothing is filtered
    /// (it is page-wide). <see cref="Severity.Unknown"/> without a reading younger than <paramref name="staleAfter"/>.
    /// </summary>
    public static ServiceState Evaluate(
        CatalogEntry entry, StatusFilter filter, ServiceReading? reading, DateTimeOffset? readAt, string? error,
        DateTimeOffset now, TimeSpan staleAfter)
    {
        if (reading is null || readAt is not { } at || now - at > staleAfter)
            return new ServiceState(entry, Severity.Unknown, [], [], [], readAt, error);

        var affected = reading.Components.Where(c => c.Severity > Severity.Operational && filter.Matches(c))
            .OrderByDescending(c => c.Severity).ToList();
        var incidents = reading.Incidents.Where(i => Relevant(filter, i.Components, i.Locations))
            .OrderByDescending(i => i.Severity).ToList();
        var maintenances = reading.Maintenances
            .Where(m => (m.InProgress || m.Start is not { } start || start - now <= MaintenanceHorizon) && (m.End is not { } end || end > now))
            .Where(m => Relevant(filter, m.Components, []))
            .OrderByDescending(m => m.InProgress).ThenBy(m => m.Start).ToList();

        var severity = Severity.Operational;
        foreach (var c in affected)
            severity = Providers.Max(severity, c.Severity);
        foreach (var i in incidents)
            severity = Providers.Max(severity, i.Severity);
        if (maintenances.Any(m => m.InProgress))
            severity = Providers.Max(severity, Severity.Maintenance);
        if (filter.IsAll)
            severity = Providers.Max(severity, reading.Indicator);
        return new ServiceState(entry, severity, affected, incidents, maintenances, readAt, error);
    }

    private static bool Relevant(StatusFilter filter, IReadOnlyList<ComponentReading> components, IReadOnlyList<string> locations) =>
        filter.MatchesRegions(locations) && (components.Count == 0 || components.Any(filter.Matches));

    /// <summary>Display order: worst issue first, then unknown, maintenance, operational.</summary>
    public static int Rank(Severity severity) => severity switch
    {
        Severity.MajorOutage => 5,
        Severity.PartialOutage => 4,
        Severity.Degraded => 3,
        Severity.Unknown => 2,
        Severity.Maintenance => 1,
        _ => 0,
    };

    public static string Describe(Severity severity) => severity switch
    {
        Severity.Unknown => "Unknown",
        Severity.Operational => "Operational",
        Severity.Maintenance => "Maintenance",
        Severity.Degraded => "Degraded",
        Severity.PartialOutage => "Partial outage",
        _ => "Major outage",
    };
}
