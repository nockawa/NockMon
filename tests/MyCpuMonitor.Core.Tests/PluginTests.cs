using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MyCpuMonitor.Core.Help;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Persistence;
using MyCpuMonitor.Core.Plugins;

namespace MyCpuMonitor.Core.Tests;

public sealed class PluginTests : IDisposable
{
    private static readonly Version HostApi = new(1, 0);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mycpumon-plugin-tests", Guid.NewGuid().ToString("N"));

    public PluginTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A plugin package as the SDK's pack target produces it.</summary>
    internal static byte[] Package(string id, string version, string api = "1.0", string? manifestId = null, string type = PluginPackage.PackageType,
        params (string Path, string Content)[] extra)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
            Add($"{id}.nuspec", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{id}</id><version>{version}</version><title>{id} title</title><authors>Someone</authors>
                    <description>Does things.</description><projectUrl>https://example.com/{id}</projectUrl>
                    <packageTypes><packageType name="{type}" /></packageTypes>
                  </metadata>
                </package>
                """);
            Add("plugin/" + PluginManifest.FileName,
                $$"""{ "id": "{{manifestId ?? id}}", "version": "{{version}}", "assembly": "{{id}}.dll", "api": "{{api}}", "title": "{{id}} title" }""");
            Add($"plugin/{id}.dll", "binary");
            Add("plugin/runtimes/win-x64/native/dep%20lib.dll", "native");
            foreach (var (path, content) in extra)
                Add(path, content);
        }
        return buffer.ToArray();
    }

    [Fact]
    public void Manifest_Parses_AndRejectsMissingOrUnsafeFields()
    {
        var m = PluginManifest.Parse("""{ "id": "A", "version": "1.2.0", "assembly": "A.dll", "api": "1.0", "authors": "Me" }""");
        Assert.Equal(("A", "1.2.0", "A.dll", new Version(1, 0), "A", "Me"), (m.Id, m.Version, m.Assembly, m.Api, m.Title, m.Authors));

        Assert.Throws<InvalidDataException>(() => PluginManifest.Parse("""{ "version": "1", "assembly": "A.dll", "api": "1.0" }"""));
        Assert.Throws<InvalidDataException>(() => PluginManifest.Parse("""{ "id": "A", "version": "1", "assembly": "..\\x.dll", "api": "1.0" }"""));
        Assert.Throws<InvalidDataException>(() => PluginManifest.Parse("not json"));
    }

    [Theory]
    [InlineData("1.0", "1.0", true)]
    [InlineData("1.0", "1.3", true)]
    [InlineData("1.4", "1.3", false)] // Uses API the host does not have.
    [InlineData("2.0", "1.9", false)]
    [InlineData("1.0", "2.0", false)] // Breaking change.
    public void Compatibility_SameMajor_PluginMinorNotNewer(string plugin, string host, bool expected) =>
        Assert.Equal(expected, PluginManifest.IsCompatible(Version.Parse(plugin), Version.Parse(host)));

    [Fact]
    public void Package_RejectsNonPlugin_MismatchedIds_AndPathsEscapingTheFolder()
    {
        var notPlugin = new MemoryStream();
        using (var zip = new ZipArchive(notPlugin, ZipArchiveMode.Create, leaveOpen: true))
            using (var w = new StreamWriter(zip.CreateEntry("x.nuspec").Open()))
                w.Write("<package><metadata><id>X</id><version>1.0.0</version></metadata></package>");
        notPlugin.Position = 0;
        Assert.Contains("not a My CPU Monitor plugin", Assert.Throws<InvalidDataException>(() => PluginPackage.Read(notPlugin)).Message);

        Assert.Throws<InvalidDataException>(() => PluginPackage.Read(new MemoryStream(Package("A", "1.0.0", manifestId: "B"))));

        var evil = PluginPackage.Read(new MemoryStream(Package("A", "1.0.0", extra: ("plugin/../../evil.dll", "x"))));
        Assert.Throws<InvalidDataException>(() => evil.ExtractTo(Path.Combine(_dir, "evil")));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.dll")));
    }

    [Fact]
    public void Install_ExtractsPluginFolder_AndRecordsVersion()
    {
        var store = new PluginStore(_dir, HostApi);

        var manifest = store.Install(new MemoryStream(Package("Acme.Widgets", "1.0")), "nuget.org");

        var installed = Assert.Single(store.List());
        Assert.Equal(("Acme.Widgets", "1.0.0", "nuget.org", false), (installed.Id, installed.Version, installed.Source, installed.PendingRemoval));
        Assert.Equal("Acme.Widgets.dll", manifest.Assembly);
        Assert.True(File.Exists(Path.Combine(installed.Directory, "Acme.Widgets.dll")));
        Assert.True(File.Exists(Path.Combine(installed.Directory, "runtimes", "win-x64", "native", "dep lib.dll"))); // Unescaped.
        Assert.Equal(manifest, PluginManifest.Load(installed.Directory) with { Version = manifest.Version });
        Assert.Empty(Directory.GetFiles(_dir, "*.nuspec", SearchOption.AllDirectories)); // Payload only.
    }

    [Fact]
    public void Install_RejectsIncompatibleApi_WithActionableMessage()
    {
        var store = new PluginStore(_dir, HostApi);
        var error = Assert.Throws<InvalidDataException>(() => store.Install(new MemoryStream(Package("A", "2.0.0", api: "1.1")), "x"));
        Assert.Contains("Update My CPU Monitor", error.Message);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Update_KeepsRunningVersionUntilNextStart_ThenDeletesIt()
    {
        var store = new PluginStore(_dir, HostApi);
        var first = store.Install(new MemoryStream(Package("A", "1.0.0")), "feed");
        var oldDirectory = store.Find("A")!.Directory;

        store.Install(new MemoryStream(Package("A", "1.1.0")), "feed");

        Assert.Equal("1.1.0", Assert.Single(store.List()).Version);
        Assert.True(Directory.Exists(oldDirectory)); // Possibly loaded: left alone.
        store.ApplyPending();
        Assert.False(Directory.Exists(oldDirectory));
        Assert.True(Directory.Exists(store.Find("A")!.Directory));
        Assert.Equal("1.0.0", first.Version);
    }

    [Fact]
    public void Uninstall_IsPending_CanBeCancelled_AndAppliesAtNextStart()
    {
        var store = new PluginStore(_dir, HostApi);
        store.Install(new MemoryStream(Package("A", "1.0.0")), "feed");
        store.Install(new MemoryStream(Package("B", "1.0.0")), "feed");

        store.Uninstall("A");
        Assert.True(store.Find("A")!.PendingRemoval);
        store.CancelUninstall("A");
        Assert.False(store.Find("A")!.PendingRemoval);

        store.Uninstall("A");
        store.ApplyPending();
        Assert.Equal(["B"], store.List().Select(p => p.Id));
        Assert.False(Directory.Exists(Path.Combine(_dir, "A")));

        // Reinstalling after a pending uninstall keeps it.
        store.Uninstall("B");
        store.Install(new MemoryStream(Package("B", "1.0.0")), "feed");
        store.ApplyPending();
        Assert.Equal(["B"], store.List().Select(p => p.Id));
    }

    [Fact]
    public async Task FolderFeed_ListsLatestPluginPerId_FiltersPrereleaseAndQuery_AndDownloads()
    {
        var feedDir = Path.Combine(_dir, "feed");
        Directory.CreateDirectory(feedDir);
        File.WriteAllBytes(Path.Combine(feedDir, "a.1.0.0.nupkg"), Package("Acme.Cpu", "1.0.0"));
        File.WriteAllBytes(Path.Combine(feedDir, "a.1.2.0.nupkg"), Package("Acme.Cpu", "1.2.0"));
        File.WriteAllBytes(Path.Combine(feedDir, "a.2.0.0-beta.nupkg"), Package("Acme.Cpu", "2.0.0-beta"));
        File.WriteAllBytes(Path.Combine(feedDir, "lib.nupkg"), Package("Some.Library", "1.0.0", type: "Dependency"));
        File.WriteAllBytes(Path.Combine(feedDir, "other.nupkg"), Package("Zed.Gpu", "0.1.0"));
        File.WriteAllText(Path.Combine(feedDir, "broken.nupkg"), "not a zip");
        var feed = new FolderFeed(feedDir);

        var stable = await feed.SearchAsync(null, prerelease: false, default);
        Assert.Equal([("Acme.Cpu", "1.2.0"), ("Zed.Gpu", "0.1.0")], stable.Select(l => (l.Id, l.Version)).OrderBy(x => x.Id));
        Assert.Equal("2.0.0-beta", Assert.Single(await feed.SearchAsync("acme", prerelease: true, default)).Version);
        Assert.Equal("Zed.Gpu", Assert.Single(await feed.SearchAsync("zed", prerelease: false, default)).Id);

        await using var package = await feed.DownloadAsync("acme.cpu", "1.0", default);
        Assert.Equal("1.0.0", PluginPackage.Read(package).Manifest.Version);
    }

    [Fact]
    public async Task NuGetFeed_SearchesByPackageType_AndDownloadsFromFlatContainer()
    {
        var handler = new FakeHandler();
        var feed = new NuGetFeed(new HttpClient(handler), "https://feed.test/v3/index.json");

        var listings = await feed.SearchAsync("claude usage", prerelease: false, default);

        var listing = Assert.Single(listings);
        Assert.Equal(("ClaudeUsage.MyCpuMon", "1.0.0", "Claude", "a, b", 42L, true),
            (listing.Id, listing.Version, listing.Title, listing.Authors, listing.Downloads, listing.Verified));
        Assert.Contains("packageType=MyCpuMonPlugin", handler.Requests[1]);
        Assert.Contains("q=claude%20usage", handler.Requests[1]);
        Assert.Contains("prerelease=false", handler.Requests[1]);

        await using var package = await feed.DownloadAsync("ClaudeUsage.MyCpuMon", "1.0", default);
        Assert.Equal("https://feed.test/flat/claudeusage.mycpumon/1.0.0/claudeusage.mycpumon.1.0.0.nupkg", handler.Requests[^1]);
        Assert.Single(handler.Requests, r => r.EndsWith("v3/index.json")); // Service index fetched once.
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            HttpContent content = url switch
            {
                _ when url.EndsWith("v3/index.json") => new StringContent("""
                    { "resources": [
                      { "@id": "https://feed.test/query-old", "@type": "SearchQueryService" },
                      { "@id": "https://feed.test/query", "@type": "SearchQueryService/3.5.0" },
                      { "@id": "https://feed.test/flat", "@type": "PackageBaseAddress/3.0.0" } ] }
                    """),
                _ when url.StartsWith("https://feed.test/query?") => new StringContent("""
                    { "totalHits": 1, "data": [ { "id": "ClaudeUsage.MyCpuMon", "version": "1.0.0", "title": "Claude",
                      "description": "d", "authors": ["a", "b"], "totalDownloads": 42, "verified": true,
                      "packageTypes": [ { "name": "MyCpuMonPlugin" } ], "versions": [] } ] }
                    """),
                _ when url.EndsWith(".nupkg") => new ByteArrayContent(Package("ClaudeUsage.MyCpuMon", "1.0.0")),
                _ => throw new InvalidOperationException(url),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public void PluginWidget_RoundTripsSettings_AndUsesTheCatalogForSizeAndDocs()
    {
        var config = new PluginWidgetConfig
        {
            Plugin = "Test.RoundTrip",
            Widget = "weekly",
            Title = "Weekly",
            Bounds = new GridRect(0, 0, 4, 3),
            Settings = new JsonObject { ["refreshMinutes"] = 10, ["nested"] = new JsonObject { ["x"] = "y" } },
        };
        var layout = new LayoutDocument { Name = "L", Widgets = [config] };

        var back = Assert.IsType<PluginWidgetConfig>(Assert.Single(LayoutStore.Deserialize(LayoutStore.Serialize(layout)).Widgets));
        Assert.Equal(("Test.RoundTrip", "weekly"), (back.Plugin, back.Widget));
        Assert.Equal(10, (int)back.Settings!["refreshMinutes"]!);
        Assert.Equal("y", (string)back.Settings["nested"]!["x"]!);
        Assert.Equal("y", (string)LayoutStore.Clone(back).Settings!["nested"]!["x"]!);

        // Not loaded: placeholder size and an explanation.
        Assert.Equal(new GridSize(1, 1), WidgetCatalog.MinSize(back));
        Assert.Contains("not installed", Docs.Of(back).Summary);

        PluginCatalog.Register(new PluginWidgetInfo("Test.RoundTrip", "weekly", "Weekly usage", "Sum.", "Det.", new GridSize(4, 3), new GridSize(3, 2),
            "Round trip", "1.0.0", "Me"));
        Assert.Equal(new GridSize(3, 2), WidgetCatalog.MinSize(back));
        Assert.Equal("Weekly usage", WidgetCatalog.DisplayName(back));
        Assert.Contains("**Plugin:** Round trip 1.0.0 by Me", Docs.Of(back).Details);
        Assert.DoesNotContain(WidgetKind.Plugin, WidgetCatalog.Kinds);
    }
}
