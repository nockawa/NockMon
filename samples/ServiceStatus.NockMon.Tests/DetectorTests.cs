using ServiceStatus.NockMon.Status;

namespace ServiceStatus.NockMon.Tests;

public class DetectorTests
{
    private const string Summary = """{"page":{"id":"x","name":"Example","url":"https://status.example.com"},"status":{"indicator":"none"},"components":[],"incidents":[]}""";
    private const string CompatSummary = """{"page":{"id":"x","name":"Example"},"status":{"indicator":"none"},"components":[]}""";
    private const string Widget = """{"page_title":"Example","ongoing_incidents":[],"in_progress_maintenances":[],"scheduled_maintenances":[]}""";

    /// <summary>A fake web: URL → (final URL, content type, body).</summary>
    private static Func<Uri, CancellationToken, Task<Fetched?>> Web(params (string Url, string Type, string Body, string? Final)[] pages) =>
        (uri, _) => Task.FromResult(pages.Where(p => p.Url == uri.AbsoluteUri)
            .Select(p => new Fetched(new Uri(p.Final ?? p.Url), p.Type, p.Body)).FirstOrDefault());

    private static Task<(CatalogEntry? Entry, string Message)> Detect(string input, Func<Uri, CancellationToken, Task<Fetched?>> web) =>
        Detector.DetectAsync(input, web, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Statuspage()
    {
        var (entry, _) = await Detect("status.example.com", Web(("https://status.example.com/api/v2/summary.json", "application/json", Summary, null)));
        Assert.NotNull(entry);
        Assert.Equal(ProviderKind.Statuspage, entry.Kind);
        Assert.Equal("Example", entry.Name);
        Assert.Equal("https://status.example.com", entry.Url);
        Assert.Equal("custom:status.example.com", entry.Id);
        Assert.True(entry.Custom);
    }

    [Fact]
    public async Task Pasted_api_address()
    {
        var (entry, _) = await Detect("https://status.example.com/api/v2/summary.json",
            Web(("https://status.example.com/api/v2/summary.json", "application/json", Summary, null)));
        Assert.Equal("https://status.example.com", entry?.Url);
    }

    [Fact]
    public async Task IncidentIo_with_its_widget_api()
    {
        var (entry, _) = await Detect("https://status.example.com/", Web(
            ("https://status.example.com/api/v2/summary.json", "application/json; charset=utf-8", CompatSummary, null),
            ("https://status.example.com/api/v1/summary", "application/json", Widget, null)));
        Assert.Equal(ProviderKind.IncidentIo, entry?.Kind);
    }

    [Fact]
    public async Task IncidentIo_without_its_widget_api_reads_as_statuspage()
    {
        var (entry, message) = await Detect("status.example.com", Web(
            ("https://status.example.com/api/v2/summary.json", "application/json", CompatSummary, null),
            ("https://status.example.com/api/v1/summary", "text/html", "<html></html>", null)));
        Assert.Equal(ProviderKind.Statuspage, entry?.Kind);
        Assert.Contains("not its incidents", message);
    }

    [Fact]
    public async Task Follows_a_redirect_to_another_page()
    {
        var (entry, _) = await Detect("status.anthropic.com", Web(
            ("https://status.anthropic.com/api/v2/summary.json", "application/json", Summary, "https://status.claude.com/api/v2/summary.json")));
        Assert.Equal("https://status.claude.com", entry?.Url);
        Assert.Equal("custom:status.claude.com", entry?.Id);
    }

    [Fact]
    public async Task Refuses_a_redirect_to_another_api()
    {
        var (entry, message) = await Detect("status.example.com", Web(
            ("https://status.example.com/api/v2/summary.json", "application/json", Summary, "https://status.other.example/api/compat/summary.json")));
        Assert.Null(entry);
        Assert.Contains("status.other.example", message);
    }

    [Theory]
    [InlineData("http://status.example.com")]
    [InlineData("ftp://status.example.com")]
    [InlineData("")]
    public async Task Refuses_what_is_not_https(string input)
    {
        var (entry, _) = await Detect(input, Web());
        Assert.Null(entry);
    }

    [Fact]
    public async Task Html_answered_with_200_is_not_a_page()
    {
        var (entry, _) = await Detect("status.example.com", Web(("https://status.example.com/api/v2/summary.json", "text/html", "<html>{\"page\":1}</html>", null)));
        Assert.Null(entry);
    }

    [Fact]
    public async Task Json_of_another_shape_is_not_a_page()
    {
        var (entry, _) = await Detect("status.example.com", Web(("https://status.example.com/api/v2/summary.json", "application/json", """{"hello":"world"}""", null)));
        Assert.Null(entry);
    }
}
