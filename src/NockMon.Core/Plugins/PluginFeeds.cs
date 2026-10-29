using System.Text.Json;
using NuGet.Versioning;

namespace NockMon.Core.Plugins;

/// <summary>A plugin offered by a feed (latest version matching the prerelease choice).</summary>
/// <param name="Verified">nuget.org: the owner reserved the package id prefix.</param>
public sealed record PluginListing(
    string Id,
    string Version,
    string Title,
    string Description,
    string Authors,
    string? IconUrl,
    string? ProjectUrl,
    long? Downloads,
    bool Verified,
    IPluginFeed Feed);

/// <summary>A source of plugin packages.</summary>
public interface IPluginFeed
{
    /// <summary>Shown to the user and recorded as the source of installed plugins.</summary>
    string Name { get; }

    /// <summary>Plugins matching <paramref name="query"/> (all when empty), latest version each.</summary>
    Task<IReadOnlyList<PluginListing>> SearchAsync(string? query, bool prerelease, CancellationToken cancellation);

    Task<Stream> DownloadAsync(string id, string version, CancellationToken cancellation);
}

public static class PluginFeeds
{
    public const string NuGetOrg = "https://api.nuget.org/v3/index.json";

    /// <summary>A NuGet V3 feed for URLs, a folder of .nupkg files otherwise.</summary>
    public static IPluginFeed Create(string source, HttpClient http) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? new NuGetFeed(http, source)
            : new FolderFeed(source);

    internal static bool Matches(string? query, params string?[] fields) =>
        string.IsNullOrWhiteSpace(query) || query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(term => fields.Any(f => f?.Contains(term, StringComparison.OrdinalIgnoreCase) == true));
}

/// <summary>
/// NuGet V3 feed (nuget.org): search filtered on the plugin package type (SearchQueryService/3.5.0), downloads from the
/// flat container (PackageBaseAddress/3.0.0). Resource URLs come from the service index, fetched once.
/// </summary>
public sealed class NuGetFeed(HttpClient http, string indexUrl) : IPluginFeed
{
    private (string Search, string Packages)? _resources;

    public string Name => indexUrl == PluginFeeds.NuGetOrg ? "nuget.org" : indexUrl;

    public async Task<IReadOnlyList<PluginListing>> SearchAsync(string? query, bool prerelease, CancellationToken cancellation)
    {
        var (search, _) = await ResourcesAsync(cancellation);
        var url = $"{search}?q={Uri.EscapeDataString(query ?? "")}&take=100&prerelease={(prerelease ? "true" : "false")}" +
                  $"&semVerLevel=2.0.0&packageType={PluginPackage.PackageType}";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, cancellation));
        var listings = new List<PluginListing>();
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            listings.Add(new PluginListing(
                Text(item, "id")!,
                Text(item, "version")!,
                Text(item, "title") ?? Text(item, "id")!,
                Text(item, "description") ?? "",
                Authors(item),
                Text(item, "iconUrl"),
                Text(item, "projectUrl"),
                item.TryGetProperty("totalDownloads", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : null,
                item.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True,
                this));
        }
        return listings;
    }

    public async Task<Stream> DownloadAsync(string id, string version, CancellationToken cancellation)
    {
        var (_, packages) = await ResourcesAsync(cancellation);
        var lowerId = id.ToLowerInvariant();
        var lowerVersion = NuGetVersion.Parse(version).ToNormalizedString().ToLowerInvariant();
        var bytes = await http.GetByteArrayAsync($"{packages}{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg", cancellation);
        return new MemoryStream(bytes);
    }

    private async Task<(string Search, string Packages)> ResourcesAsync(CancellationToken cancellation)
    {
        if (_resources is { } cached)
            return cached;
        using var doc = JsonDocument.Parse(await http.GetStringAsync(indexUrl, cancellation));
        string? search = null, packages = null;
        foreach (var resource in doc.RootElement.GetProperty("resources").EnumerateArray())
        {
            var type = Text(resource, "@type");
            var id = Text(resource, "@id");
            if (type == "SearchQueryService/3.5.0")
                search ??= id;
            else if (type == "PackageBaseAddress/3.0.0")
                packages ??= id;
        }
        if (search is null || packages is null)
            throw new InvalidDataException($"{indexUrl} does not offer package-type search (SearchQueryService/3.5.0) and downloads.");
        _resources = (search, packages.EndsWith('/') ? packages : packages + "/");
        return _resources.Value;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>"authors" is a string or an array of strings.</summary>
    private static string Authors(JsonElement item) => item.TryGetProperty("authors", out var a)
        ? a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() ?? "",
            JsonValueKind.Array => string.Join(", ", a.EnumerateArray().Select(x => x.GetString())),
            _ => "",
        }
        : "";
}

/// <summary>A folder of .nupkg files (local testing of packages before publishing, offline installs).</summary>
public sealed class FolderFeed(string directory) : IPluginFeed
{
    public string Name => directory;

    public Task<IReadOnlyList<PluginListing>> SearchAsync(string? query, bool prerelease, CancellationToken cancellation) =>
        Task.Run<IReadOnlyList<PluginListing>>(() =>
        {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Plugin source folder not found: {directory}");
            return Packages()
                .Where(p => p.Nuspec.IsPlugin && (prerelease || !p.Version.IsPrerelease))
                .GroupBy(p => p.Nuspec.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.MaxBy(p => p.Version)!)
                .Where(p => PluginFeeds.Matches(query, p.Nuspec.Id, p.Nuspec.Title, p.Nuspec.Description, p.Nuspec.Authors))
                .Select(p => new PluginListing(p.Nuspec.Id, p.Nuspec.Version, p.Nuspec.Title, p.Nuspec.Description, p.Nuspec.Authors,
                    p.Nuspec.IconUrl, p.Nuspec.ProjectUrl, null, false, this))
                .ToList();
        }, cancellation);

    public Task<Stream> DownloadAsync(string id, string version, CancellationToken cancellation) =>
        Task.Run<Stream>(() =>
        {
            var wanted = NuGetVersion.Parse(version);
            foreach (var package in Packages())
            {
                if (string.Equals(package.Nuspec.Id, id, StringComparison.OrdinalIgnoreCase) && package.Version == wanted)
                    return new MemoryStream(File.ReadAllBytes(package.Path));
            }
            throw new FileNotFoundException($"{id} {version} not found in {directory}.");
        }, cancellation);

    private IEnumerable<(string Path, NuspecInfo Nuspec, NuGetVersion Version)> Packages()
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.nupkg"))
        {
            NuspecInfo? nuspec;
            try
            {
                nuspec = PluginPackage.ReadNuspec(path);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException)
            {
                continue; // Not a readable package: ignored.
            }
            if (nuspec is not null && NuGetVersion.TryParse(nuspec.Version, out var version))
                yield return (path, nuspec, version);
        }
    }
}
