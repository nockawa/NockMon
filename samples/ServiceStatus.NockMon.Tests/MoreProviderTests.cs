using ServiceStatus.NockMon.Status;
using static ServiceStatus.NockMon.Tests.ProviderTests;

namespace ServiceStatus.NockMon.Tests;

public class MoreProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static ServiceReading Parse(CatalogEntry entry, string body) => Providers.For(entry.Kind).Parse(entry, [body]);

    private static ServiceState Evaluate(CatalogEntry entry, ServiceReading reading, StatusFilter? filter = null) =>
        Evaluation.Evaluate(entry, filter ?? entry.DefaultFilter, reading, Now, null, Now, TimeSpan.FromMinutes(15));

    [Fact]
    public void StatusIo_active_incident()
    {
        // Captured with CI/CD latency being monitored on GitLab.com.
        var gitlab = Entry("gitlab");
        var reading = Parse(gitlab, Fixture("gitlab-incident.json"));
        Assert.Equal(Severity.Degraded, reading.Indicator);
        Assert.Contains(reading.Components, c => c.Name == "Git Operations" && c.Severity == Severity.Operational);
        var incident = Assert.Single(reading.Incidents);
        Assert.Equal("Elevated CI/CD latency on GitLab.com", incident.Name);
        Assert.Equal("monitoring", incident.Status);
        Assert.Equal(Severity.Degraded, incident.Severity);
        Assert.StartsWith("We've applied a mitigation", incident.LatestUpdate);
        Assert.Equal("https://status.io/pages/incident/5b36dc6502d06804c08349f7/6ae96be253636805c01bc4ec", incident.Url);
        Assert.Equal(["CI/CD"], incident.Components.Select(c => c.Name));
        Assert.Equal(Severity.Degraded, Evaluate(gitlab, reading).Severity);
        Assert.Equal(Severity.Operational, Evaluate(gitlab, reading, new StatusFilter(["Git Operations"], [], [])).Severity);
    }

    [Fact]
    public void StatusIo_codes()
    {
        var gitlab = Entry("gitlab");
        var json = """
            {"result":{"status_overall":{"status":"Service Disruption","status_code":500},
             "status":[{"name":"API","status_code":500},{"name":"Website","status_code":200},{"name":"Pages","status_code":600}],
             "incidents":[{"name":"Old","current_active":false,"messages":[]}],
             "maintenance":{"active":[{"_id":"m1","name":"Database upgrade","datetime_planned_start":"2026-11-03T06:00:00Z","datetime_planned_end":"2026-11-03T09:00:00Z","components_affected":[{"name":"Website"}]}],"upcoming":[]}}}
            """;
        var reading = Parse(gitlab, json);
        Assert.Equal([Severity.MajorOutage, Severity.Maintenance, Severity.PartialOutage], reading.Components.Select(c => c.Severity));
        Assert.Empty(reading.Incidents);
        var maintenance = Assert.Single(reading.Maintenances);
        Assert.True(maintenance.InProgress);
        Assert.Equal("https://status.io/pages/maintenance/5b36dc6502d06804c08349f7/m1", maintenance.Url);
        Assert.Equal(Severity.MajorOutage, Evaluate(gitlab, reading).Severity);
    }

    [Fact]
    public void Heroku_systems_and_stale_maintenance()
    {
        // Captured with all systems green, and a September maintenance still listed, unresolved, with a "resolved" update.
        var heroku = Entry("heroku");
        var reading = Parse(heroku, Fixture("heroku.json"));
        Assert.Equal(["Apps", "Data", "Tools"], reading.Components.Select(c => c.Name));
        Assert.Empty(reading.Maintenances);
        Assert.Equal(Severity.Operational, Evaluate(heroku, reading).Severity);

        var json = """
            {"status":[{"system":"Apps","status":"red"},{"system":"Data","status":"green"},{"system":"Tools","status":"yellow"}],
             "incidents":[{"title":"Increased error rates","state":"open","resolved":false,"created_at":"2026-11-03T07:00:00Z","full_url":"https://status.heroku.com/incidents/1",
               "systems":[{"name":"Apps","status":"red"}],
               "updates":[{"created_at":"2026-11-03T07:00:00Z","update_type":"issue","contents":"Investigating."},{"created_at":"2026-11-03T07:30:00Z","update_type":"update","contents":"Fix in progress."}]}],
             "scheduled":[]}
            """;
        reading = Parse(heroku, json);
        var incident = Assert.Single(reading.Incidents);
        Assert.Equal(Severity.MajorOutage, incident.Severity);
        Assert.Equal("Fix in progress.", incident.LatestUpdate);
        Assert.Equal("https://status.heroku.com/incidents/1", incident.Url);
        Assert.Equal(Severity.Degraded, Evaluate(heroku, reading, new StatusFilter(["Tools"], [], [])).Severity);
        Assert.Equal(Severity.MajorOutage, Evaluate(heroku, reading).Severity);
    }

    [Fact]
    public void Azure_devops_per_geography()
    {
        // Captured with Boards, Repos, Pipelines and Test Plans degraded in Europe, an advisory on core services in Australia.
        var azdo = Entry("azure-devops");
        var reading = Parse(azdo, Fixture("azure-devops-degraded.json"));
        Assert.Equal(Severity.Degraded, reading.Indicator);
        Assert.Contains(reading.Components, c => c is { Name: "Repos", Group: "Europe", Severity: Severity.Degraded });
        Assert.Empty(reading.Incidents); // The components explain it.
        Assert.Equal(Severity.Degraded, Evaluate(azdo, reading).Severity);
        Assert.Equal(Severity.Operational, Evaluate(azdo, reading, new StatusFilter(["United States"], [], [])).Severity);
        Assert.Equal(Severity.Degraded, Evaluate(azdo, reading, new StatusFilter(["Europe"], [], [])).Severity);
    }

    [Fact]
    public void Azure_devops_message_without_component()
    {
        var azdo = Entry("azure-devops");
        var json = """{"lastUpdated":"2026-11-03T04:37:49Z","status":{"health":"unhealthy","message":"Investigating sign-in failures"},"services":[{"id":"Boards","geographies":[{"id":"US","name":"United States","health":"healthy"}]}]}""";
        var reading = Parse(azdo, json);
        var incident = Assert.Single(reading.Incidents);
        Assert.Equal("Investigating sign-in failures", incident.Name);
        // Names no component: counts whatever the filter.
        Assert.Equal(Severity.MajorOutage, Evaluate(azdo, reading, new StatusFilter(["United States"], [], [])).Severity);
    }

    [Fact]
    public async Task Detects_a_status_io_page()
    {
        var html = """<html><head><title>GitLab System Status</title></head><body><a href="https://status.io/pages/5b36dc6502d06804c08349f7/rss">RSS</a></body></html>""";
        Task<Fetched?> Web(Uri uri, CancellationToken _) => Task.FromResult<Fetched?>(uri.AbsoluteUri switch
        {
            "https://status.gitlab.com/api/v2/summary.json" => new Fetched(uri, "text/html", "<html>not found</html>"),
            "https://status.gitlab.com/" => new Fetched(uri, "text/html", html),
            "https://status.io/1.0/status/5b36dc6502d06804c08349f7" => new Fetched(uri, "application/json", Fixture("gitlab-incident.json")),
            _ => null,
        });
        var (entry, _) = await Detector.DetectAsync("status.gitlab.com", Web, TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal(ProviderKind.StatusIo, entry.Kind);
        Assert.Equal("GitLab System Status", entry.Name);
        Assert.Equal("https://status.io/1.0/status/5b36dc6502d06804c08349f7", entry.Url);
        Assert.Equal("https://status.gitlab.com/", entry.PageUrl);
    }

    [Fact]
    public void Watchlist_round_trip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nockmon-watchlist-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Empty(Watchlist.Load(dir));
            Watchlist.Save(dir,
            [
                new Watched("github", StatusFilter.All, History: true),
                new Watched("aws", new StatusFilter([], [], ["us-east-1"]), History: true),
                new Watched("npm", StatusFilter.All, History: false), // Not watched.
            ]);
            var loaded = Watchlist.Load(dir);
            Assert.Equal(["github", "aws"], loaded.Select(w => w.Id));
            Assert.All(loaded, w => Assert.True(w.History));
            Assert.Equal(["us-east-1"], loaded[1].Filter.Regions);
            Assert.True(loaded[0].Filter.IsAll);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(Severity.Operational, 0)]
    [InlineData(Severity.Maintenance, 1)]
    [InlineData(Severity.Degraded, 2)]
    [InlineData(Severity.PartialOutage, 3)]
    [InlineData(Severity.MajorOutage, 4)]
    public void Metric_values(Severity severity, double value) => Assert.Equal(value, Watchlist.Value(severity));

    [Fact]
    public void Unknown_is_missing_not_fine() => Assert.True(double.IsNaN(Watchlist.Value(Severity.Unknown)));

    [Fact]
    public void Metric_ids_have_no_slash() => Assert.Equal("custom:status.example.com.eu", Watchlist.MetricId("custom:status.example.com/eu"));
}
