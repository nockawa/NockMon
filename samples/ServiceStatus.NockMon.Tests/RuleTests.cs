using System.Net;
using ServiceStatus.NockMon.Status;

namespace ServiceStatus.NockMon.Tests;

public class RuleTests
{
    [Theory]
    [InlineData("*", "anything", true)]
    [InlineData("us-*", "us-east-1", true)]
    [InlineData("US-*", "us-east-1", true)]
    [InlineData("us-*", "eu-west-1", false)]
    [InlineData("eu-west-?", "eu-west-3", true)]
    [InlineData("eu-west-?", "eu-west-10", false)]
    [InlineData("Cloudflare Sites and Services", "Cloudflare Sites and Services", true)]
    [InlineData("*Outage*", "Partial Outage here", true)]
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    public void Glob(string pattern, string text, bool expected) => Assert.Equal(expected, Status.Glob.Matches(pattern, text));

    [Fact]
    public void Glob_split() => Assert.Equal(["us-east-1", "eu-*", "x"], Status.Glob.Split(" us-east-1; eu-* ,x;; "));

    [Fact]
    public void Filter_matches_name_or_group_and_excludes()
    {
        var filter = new StatusFilter(["Europe", "API"], ["Kyiv*"], []);
        Assert.True(filter.Matches(new ComponentReading("Amsterdam", "Europe", Severity.Degraded)));
        Assert.True(filter.Matches(new ComponentReading("API", null, Severity.Degraded)));
        Assert.False(filter.Matches(new ComponentReading("Kyiv, Ukraine", "Europe", Severity.Degraded)));
        Assert.False(filter.Matches(new ComponentReading("Dallas", "North America", Severity.Degraded)));
        Assert.True(StatusFilter.All.Matches(new ComponentReading("x", null, Severity.Degraded)));
    }

    [Fact]
    public void A_stale_reading_is_unknown_never_fine()
    {
        var entry = ProviderTests.Entry("claude");
        var reading = new ServiceReading(Severity.Operational, null, [], [], [], true);
        var now = DateTimeOffset.UtcNow;
        var fresh = Evaluation.Evaluate(entry, StatusFilter.All, reading, now.AddMinutes(-5), null, now, TimeSpan.FromMinutes(15));
        Assert.Equal(Severity.Operational, fresh.Severity);
        var stale = Evaluation.Evaluate(entry, StatusFilter.All, reading, now.AddMinutes(-20), "HTTP 503", now, TimeSpan.FromMinutes(15));
        Assert.Equal(Severity.Unknown, stale.Severity);
        Assert.Equal("HTTP 503", stale.Error);
        Assert.False(stale.IsIssue);
        Assert.Equal(Severity.Unknown, Evaluation.Evaluate(entry, StatusFilter.All, null, null, null, now, TimeSpan.FromMinutes(15)).Severity);
    }

    [Fact]
    public void The_page_indicator_counts_only_unfiltered()
    {
        var entry = ProviderTests.Entry("github");
        var reading = new ServiceReading(Severity.PartialOutage, null, [new ComponentReading("API", null, Severity.Operational)], [], [], true);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(Severity.PartialOutage, Evaluation.Evaluate(entry, StatusFilter.All, reading, now, null, now, TimeSpan.FromMinutes(15)).Severity);
        Assert.Equal(Severity.Operational, Evaluation.Evaluate(entry, new StatusFilter(["API"], [], []), reading, now, null, now, TimeSpan.FromMinutes(15)).Severity);
    }

    [Fact]
    public void Display_order()
    {
        var order = Enum.GetValues<Severity>().OrderByDescending(Evaluation.Rank).ToArray();
        Assert.Equal([Severity.MajorOutage, Severity.PartialOutage, Severity.Degraded, Severity.Unknown, Severity.Maintenance, Severity.Operational], order);
    }

    [Theory]
    [InlineData("https://stspg.io/abc", "https://stspg.io/abc")]
    [InlineData("https://www.githubstatus.com/incidents/x", "https://www.githubstatus.com/incidents/x")]
    [InlineData("incidents/x", "https://www.githubstatus.com/incidents/x")]
    [InlineData("http://www.githubstatus.com/incidents/x", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///c:/windows", null)]
    [InlineData("https://githubstatus.com.evil.example/x", null)]
    [InlineData("https://evil.example/x", null)]
    [InlineData("", null)]
    public void Links_stay_on_the_page(string url, string? expected) => Assert.Equal(expected, Providers.Link(url, ProviderTests.Entry("github")));

    [Fact]
    public void Plain_text()
    {
        Assert.Equal("Fix deployed & monitoring", Providers.Plain("<p>Fix <b>deployed</b> &amp; monitoring</p>"));
        Assert.Equal("Summary Text in bold and code", Providers.Plain("## Summary\nText in **bold** and `code`"));
        Assert.Null(Providers.Plain("  <br/> "));
        Assert.Equal("abc…", Providers.Plain("abcdef", 3));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.10.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("fe80::1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2606:4700::1111", false)]
    public void Blocked_addresses(string address, bool blocked) => Assert.Equal(blocked, StatusHttp.IsBlocked(IPAddress.Parse(address)));

    [Fact]
    public async Task Http_refuses_what_is_not_https()
    {
        var result = await StatusHttp.GetAsync("http://example.com/api/v2/summary.json", null, null, TestContext.Current.CancellationToken);
        Assert.Null(result.Body);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Catalog_is_valid()
    {
        var entries = Catalog.BuiltIn;
        Assert.True(entries.Count >= 35);
        Assert.Equal(entries.Count, entries.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(entries, e =>
        {
            Assert.True(Catalog.IsHttps(e.Url), e.Id);
            Assert.True(Catalog.IsHttps(e.PageUrl), e.Id);
            Assert.False(e.Custom);
        });
        foreach (var kind in Enum.GetValues<ProviderKind>())
            Assert.Contains(entries, e => e.Kind == kind);
        Assert.Equal(["Cloudflare Sites and Services"], entries.Single(e => e.Id == "cloudflare").DefaultFilter.Include);
    }

    [Fact]
    public void Catalog_parse_skips_invalid_entries()
    {
        var entries = Catalog.Parse("""
            [{"id":"ok","name":"Ok","kind":"Statuspage","url":"https://status.example.com","page":"https://status.example.com"},
             {"id":"http","name":"Http","kind":"Statuspage","url":"http://status.example.com","page":"https://status.example.com"},
             {"id":"kind","name":"Kind","kind":"Gopher","url":"https://status.example.com","page":"https://status.example.com"},
             {"name":"No id","kind":"Statuspage","url":"https://status.example.com","page":"https://status.example.com"},
             42]
            """, custom: true);
        var entry = Assert.Single(entries);
        Assert.Equal("ok", entry.Id);
        Assert.True(entry.Custom);
        Assert.Throws<FormatException>(() => Catalog.Parse("{}", custom: true));
    }

    [Fact]
    public void User_catalog_adds_and_replaces()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nockmon-status-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Empty(Catalog.LoadUser(dir));
            var mine = new CatalogEntry("custom:status.example.com", "Example", ProviderKind.Statuspage, "https://status.example.com", "https://status.example.com", StatusFilter.All);
            Catalog.AddUser(dir, mine);
            Catalog.AddUser(dir, mine with { Name = "Example 2" });
            var user = Assert.Single(Catalog.LoadUser(dir));
            Assert.Equal("Example 2", user.Name);
            Assert.True(user.Custom);
            Catalog.AddUser(dir, ProviderTests.Entry("github") with { Name = "My GitHub" });
            var all = Catalog.Load(dir);
            Assert.Equal(Catalog.BuiltIn.Count + 1, all.Count);
            Assert.Equal("My GitHub", all.Single(e => e.Id == "github").Name);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
