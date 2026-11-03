using System.Text;
using ServiceStatus.NockMon.Status;

namespace ServiceStatus.NockMon.Tests;

public class ProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    internal static CatalogEntry Entry(string id) => Catalog.BuiltIn.Single(e => e.Id == id);

    private static ServiceReading Parse(CatalogEntry entry, params string[] bodies) => Providers.For(entry.Kind).Parse(entry, bodies);

    private static ServiceState Evaluate(CatalogEntry entry, ServiceReading reading, StatusFilter? filter = null) =>
        Evaluation.Evaluate(entry, filter ?? entry.DefaultFilter, reading, Now, null, Now, TimeSpan.FromMinutes(15));

    [Fact]
    public void Statuspage_all_operational()
    {
        var claude = Entry("claude");
        var reading = Parse(claude, Fixture("claude.json"));
        Assert.Equal(Severity.Operational, reading.Indicator);
        Assert.Equal(6, reading.Components.Count);
        Assert.True(reading.ListsComponents);
        Assert.Contains(reading.Components, c => c.Name == "Claude Code");
        Assert.Equal(Severity.Operational, Evaluate(claude, reading).Severity);
    }

    [Fact]
    public void Cloudflare_counts_its_core_services_only_by_default()
    {
        var cloudflare = Entry("cloudflare");
        var reading = Parse(cloudflare, Fixture("cloudflare-trimmed.json"));
        // Captured with the page at "major" and points of presence out: their normal state.
        Assert.Equal(Severity.PartialOutage, reading.Indicator);
        Assert.Contains(reading.Components, c => c is { Group: "North America", Severity: Severity.MajorOutage });
        Assert.DoesNotContain(reading.Components, c => c.Name == "Europe"); // Group headers repeat their members.

        var core = Evaluate(cloudflare, reading);
        Assert.Equal(Severity.Operational, core.Severity);
        Assert.Empty(core.Affected);
        Assert.Empty(core.Maintenances); // Seattle's maintenance is in North America.

        var everything = Evaluate(cloudflare, reading, StatusFilter.All);
        Assert.Equal(Severity.MajorOutage, everything.Severity);
        Assert.Equal(Severity.MajorOutage, everything.Affected[0].Severity);

        var europe = Evaluate(cloudflare, reading, new StatusFilter(["Europe"], [], []));
        Assert.Equal(Severity.Degraded, europe.Severity);
        Assert.All(europe.Affected, c => Assert.Equal("Europe", c.Group));
    }

    [Fact]
    public void Statuspage_open_incidents_count_at_least_as_degraded()
    {
        var github = Entry("github");
        var json = """
            {"page":{"id":"x","name":"GitHub","url":"https://www.githubstatus.com"},
             "status":{"indicator":"none","description":"All Systems Operational"},
             "components":[
               {"id":"a","name":"Git Operations","status":"operational","group":false,"showcase":true},
               {"id":"b","name":"Actions","status":"operational","group":false,"showcase":true},
               {"id":"h","name":"Visit www.githubstatus.com","status":"operational","showcase":false}],
             "incidents":[
               {"name":"Disruption with some <b>GitHub</b> services","status":"investigating","impact":"none",
                "started_at":"2026-11-03T08:00:00.000Z","shortlink":"https://stspg.io/abc",
                "incident_updates":[{"body":"We are **investigating** reports &amp; will update."},{"body":"Older"}],
                "components":[]},
               {"name":"Actions delayed","status":"identified","impact":"major","shortlink":"javascript:alert(1)",
                "components":[{"id":"b","name":"Actions","status":"partial_outage"}]},
               {"name":"Old","status":"resolved","impact":"critical"}],
             "scheduled_maintenances":[]}
            """;
        var reading = Parse(github, json);
        Assert.Equal(2, reading.Components.Count);
        Assert.Equal(2, reading.Incidents.Count);
        var first = reading.Incidents[0];
        Assert.Equal("Disruption with some GitHub services", first.Name);
        Assert.Equal(Severity.Degraded, first.Severity);
        Assert.Equal("We are investigating reports & will update.", first.LatestUpdate);
        Assert.Equal("https://stspg.io/abc", first.Url);
        Assert.Null(reading.Incidents[1].Url);

        Assert.Equal(Severity.PartialOutage, Evaluate(github, reading).Severity);
        // Following Git only: the Actions incident doesn't count, the one naming no component does.
        var git = Evaluate(github, reading, new StatusFilter(["Git Operations"], [], []));
        Assert.Equal(Severity.Degraded, git.Severity);
        Assert.Single(git.Incidents);
    }

    [Fact]
    public void Statuspage_maintenance_is_shown_not_an_issue()
    {
        var npm = Entry("npm");
        var json = $$"""
            {"page":{"id":"x"},"status":{"indicator":"maintenance","description":"Under maintenance"},
             "components":[{"id":"r","name":"Registry","status":"under_maintenance"}],
             "incidents":[],
             "scheduled_maintenances":[
               {"name":"Registry upgrade","status":"in_progress","scheduled_for":"{{Now.AddMinutes(-10):O}}","scheduled_until":"{{Now.AddHours(1):O}}","components":[]},
               {"name":"Next week","status":"scheduled","scheduled_for":"{{Now.AddDays(7):O}}","scheduled_until":"{{Now.AddDays(7).AddHours(1):O}}","components":[]},
               {"name":"Tonight","status":"scheduled","scheduled_for":"{{Now.AddHours(5):O}}","scheduled_until":"{{Now.AddHours(6):O}}","components":[]}]}
            """;
        var state = Evaluate(npm, Parse(npm, json));
        Assert.Equal(Severity.Maintenance, state.Severity);
        Assert.False(state.IsIssue);
        Assert.Equal(["Registry upgrade", "Tonight"], state.Maintenances.Select(m => m.Name));
    }

    [Fact]
    public void Compatible_summary_without_incidents()
    {
        var docker = Entry("docker");
        var reading = Parse(docker, Fixture("docker.json"));
        Assert.NotEmpty(reading.Components);
        Assert.Empty(reading.Incidents);
    }

    [Fact]
    public void IncidentIo_components_and_ongoing_incidents()
    {
        var openai = Entry("openai");
        var quiet = Parse(openai, Fixture("openai-v2.json"), Fixture("openai-v1.json"));
        Assert.NotEmpty(quiet.Components);
        Assert.Empty(quiet.Incidents);
        Assert.Equal(Severity.Operational, Evaluate(openai, quiet).Severity);

        var widget = """
            {"page_title":"OpenAI","page_url":"https://status.openai.com/",
             "ongoing_incidents":[
               {"id":"1","name":"Elevated errors","status":"identified","url":"https://status.openai.com/incidents/1",
                "last_update_at":"2026-11-03T08:10:00Z","last_update_message":"A fix is rolling out.","current_worst_impact":"full_outage",
                "affected_components":[{"id":"c","name":"Chat Completions","current_status":"full_outage","group_name":"APIs"}]},
               {"id":"2","name":"Monitoring","status":"monitoring","url":"https://evil.example.com/x","current_worst_impact":"operational","affected_components":[]}],
             "in_progress_maintenances":[],"scheduled_maintenances":[]}
            """;
        var reading = Parse(openai, Fixture("openai-v2.json"), widget);
        Assert.Equal(Severity.MajorOutage, reading.Incidents[0].Severity);
        Assert.Equal("https://status.openai.com/incidents/1", reading.Incidents[0].Url);
        Assert.Equal("APIs", reading.Incidents[0].Components[0].Group);
        Assert.Equal(Severity.Degraded, reading.Incidents[1].Severity); // Still open: not fine yet.
        Assert.Null(reading.Incidents[1].Url); // Not the page's host.
        Assert.Equal(Severity.MajorOutage, Evaluate(openai, reading).Severity);
    }

    [Fact]
    public void IncidentIo_without_widget_api_is_an_error()
    {
        var openai = Entry("openai");
        Assert.Throws<FormatException>(() => Parse(openai, Fixture("openai-v2.json"), "<html>404</html>"));
    }

    [Fact]
    public void Slack_types_and_maintenance()
    {
        var slack = Entry("slack");
        Assert.Equal(Severity.Operational, Evaluate(slack, Parse(slack, Fixture("slack-ok.json"))).Severity);
        var json = """
            {"status":"active","date_created":"2026-11-03T08:00:00-07:00","date_updated":"2026-11-03T08:30:00-07:00",
             "active_incidents":[
               {"id":"1","title":"Messages failing to send","type":"outage","status":"active","url":"https://slack-status.com/2026-11/abc",
                "services":["Messaging","Connectivity"],"notes":[
                  {"date_created":"2026-11-03T08:05:00-07:00","body":"Investigating."},
                  {"date_created":"2026-11-03T08:25:00-07:00","body":"<p>Fix deployed, monitoring.</p>"}]},
               {"id":"2","title":"Database upgrade","type":"notice","status":"scheduled","services":["Huddles"],"notes":[]}]}
            """;
        var reading = Parse(slack, json);
        var outage = Assert.Single(reading.Incidents);
        Assert.Equal(Severity.MajorOutage, outage.Severity);
        Assert.Equal("Fix deployed, monitoring.", outage.LatestUpdate);
        Assert.Equal(["Messaging", "Connectivity"], outage.Components.Select(c => c.Name));
        Assert.Single(reading.Maintenances);
        Assert.Equal(Severity.Operational, Evaluate(slack, reading, new StatusFilter(["Huddles"], [], [])).Severity);
        Assert.Equal(Severity.MajorOutage, Evaluate(slack, reading).Severity);
    }

    [Fact]
    public void Google_open_incidents_products_and_regions()
    {
        var gcp = Entry("google-cloud");
        Assert.Empty(Parse(gcp, Fixture("gcp-closed.json")).Incidents); // All ended.
        var json = """
            [{"id":"x","begin":"2026-11-03T07:00:00+00:00","modified":"2026-11-03T08:00:00+00:00",
              "external_desc":"Cloud SQL connection errors","status_impact":"SERVICE_DISRUPTION","severity":"medium","uri":"incidents/x",
              "affected_products":[{"title":"Cloud SQL","id":"a"}],
              "currently_affected_locations":[{"title":"Belgium (europe-west1)","id":"europe-west1"}],
              "most_recent_update":{"text":"## Summary\nWe are **investigating**.","status":"SERVICE_DISRUPTION"}},
             {"id":"y","begin":"2026-11-02T07:00:00+00:00","end":"2026-11-02T09:00:00+00:00","external_desc":"Over","status_impact":"SERVICE_OUTAGE","affected_products":[]}]
            """;
        var reading = Parse(gcp, json);
        var incident = Assert.Single(reading.Incidents);
        Assert.Equal(Severity.PartialOutage, incident.Severity);
        Assert.Equal("https://status.cloud.google.com/incidents/x", incident.Url);
        Assert.Equal("Summary We are investigating.", incident.LatestUpdate);
        Assert.Contains("europe-west1", incident.Locations);
        Assert.Equal(Severity.PartialOutage, Evaluate(gcp, reading, new StatusFilter([], [], ["europe-*"])).Severity);
        Assert.Equal(Severity.Operational, Evaluate(gcp, reading, new StatusFilter([], [], ["us-*"])).Severity);
        Assert.Equal(Severity.Operational, Evaluate(gcp, reading, new StatusFilter(["Compute Engine"], [], [])).Severity);
    }

    [Fact]
    public void Aws_utf16_feed_with_regions()
    {
        var aws = Entry("aws");
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "aws-currentevents.json"));
        var reading = Parse(aws, StatusHttp.Decode(bytes, "utf-16"));
        Assert.Equal(2, reading.Incidents.Count);
        Assert.All(reading.Incidents, i => Assert.Equal(Severity.MajorOutage, i.Severity));
        Assert.Contains(reading.Incidents, i => i.Locations.Contains("me-central-1") && i.Name.Contains("UAE"));
        Assert.All(reading.Incidents, i => Assert.NotNull(i.LatestUpdate));
        // The captured events are months old, in the Middle East: a region filter is what keeps AWS meaningful.
        Assert.Equal(Severity.MajorOutage, Evaluate(aws, reading).Severity);
        Assert.Equal(Severity.Operational, Evaluate(aws, reading, new StatusFilter([], [], ["us-*", "eu-*"])).Severity);
        Assert.Equal(Severity.MajorOutage, Evaluate(aws, reading, new StatusFilter([], [], ["me-south-1"])).Severity);
    }

    [Fact]
    public void Azure_rss()
    {
        var azure = Entry("azure");
        Assert.Empty(Parse(azure, Fixture("azure-empty.rss")).Incidents);
        var rss = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0"><channel><title>Azure Status</title>
              <item><title>Storage - West Europe</title><link>https://azure.status.microsoft/en-us/status/history/</link>
                <description>&lt;p&gt;Customers may see &lt;b&gt;errors&lt;/b&gt;.&lt;/p&gt;</description><pubDate>Tue, 03 Nov 2026 07:24:00 Z</pubDate></item>
            </channel></rss>
            """;
        var incident = Assert.Single(Parse(azure, rss).Incidents);
        Assert.Equal("Storage - West Europe", incident.Name);
        Assert.Equal("Customers may see errors.", incident.LatestUpdate);
        Assert.Equal(Severity.PartialOutage, incident.Severity);
        Assert.NotNull(incident.StartedAt);
    }

    [Fact]
    public void Azure_rss_refuses_a_dtd()
    {
        var xxe = """<?xml version="1.0"?><!DOCTYPE r [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]><rss><channel><item><title>&x;</title></item></channel></rss>""";
        Assert.Throws<FormatException>(() => Parse(Entry("azure"), xxe));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"page":{}}""")]
    public void Statuspage_rejects_what_is_not_a_summary(string body) =>
        Assert.Throws<FormatException>(() => Parse(Entry("github"), body));

    [Fact]
    public void Bounds_on_what_a_page_brings()
    {
        var components = string.Join(",", Enumerable.Range(0, 5000).Select(i => $$"""{"id":"{{i}}","name":"c{{i}}","status":"major_outage"}"""));
        var incidents = string.Join(",", Enumerable.Range(0, 500).Select(i => $$"""{"name":"i{{i}}","status":"investigating","impact":"major","incident_updates":[{"body":"{{new string('x', 5000)}}"}]}"""));
        var reading = Parse(Entry("github"), $$"""{"status":{"indicator":"major"},"components":[{{components}}],"incidents":[{{incidents}}]}""");
        Assert.Equal(Providers.MaxComponents, reading.Components.Count);
        Assert.Equal(Providers.MaxIncidents, reading.Incidents.Count);
        Assert.True(reading.Incidents[0].LatestUpdate!.Length <= 601);
    }

    [Fact]
    public void Deep_json_is_refused() =>
        Assert.Throws<FormatException>(() => Parse(Entry("github"), new string('[', 100) + new string(']', 100)));

    [Fact]
    public void Decode_by_charset_and_byte_order_mark()
    {
        Assert.Equal("{\"a\":1}", StatusHttp.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("{\"a\":1}")], null));
        Assert.Equal("{\"a\":1}", StatusHttp.Decode(Encoding.Unicode.GetBytes("{\"a\":1}"), "utf-16"));
        Assert.Equal("é", StatusHttp.Decode(Encoding.UTF8.GetBytes("é"), null));
        Assert.Equal("é", StatusHttp.Decode(Encoding.UTF8.GetBytes("é"), "no-such-charset"));
    }
}
