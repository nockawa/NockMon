using System.Text.Json;
using System.Text.Json.Nodes;

namespace ServiceStatus.NockMon.Status;

/// <summary>
/// The status pages offered: the built-in ones (Catalog.json, embedded: updated with the plugin, never fetched) and the
/// ones the user added by URL (catalog.user.json in the plugin's folder, which only administrators can write). A user
/// entry with a built-in id replaces it.
/// </summary>
public static class Catalog
{
    private const string UserFile = "catalog.user.json";
    private static readonly Lazy<IReadOnlyList<CatalogEntry>> Embedded = new(LoadEmbedded);

    public static IReadOnlyList<CatalogEntry> BuiltIn => Embedded.Value;

    /// <summary>Built-in and user entries, by name.</summary>
    public static IReadOnlyList<CatalogEntry> Load(string dataDirectory)
    {
        var entries = BuiltIn.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var user in LoadUser(dataDirectory))
            entries[user.Id] = user;
        return entries.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static IReadOnlyList<CatalogEntry> LoadUser(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, UserFile);
            return File.Exists(path) ? Parse(File.ReadAllText(path), custom: true) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return [];
        }
    }

    /// <summary>Adds (or replaces, by id) a user entry.</summary>
    public static void AddUser(string dataDirectory, CatalogEntry entry)
    {
        var entries = LoadUser(dataDirectory).Where(e => !e.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase)).Append(entry with { Custom = true });
        var array = new JsonArray(entries.Select(e => (JsonNode)new JsonObject
        {
            ["id"] = e.Id,
            ["name"] = e.Name,
            ["kind"] = e.Kind.ToString(),
            ["url"] = e.Url,
            ["page"] = e.PageUrl,
        }).ToArray());
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, UserFile), array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Entries of a catalog file; an entry that isn't valid (https URLs, known kind, id and name) is skipped.</summary>
    public static IReadOnlyList<CatalogEntry> Parse(string json, bool custom)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            throw new FormatException("Not a catalog.", e);
        }
        if (root is not JsonArray array)
            throw new FormatException("Not a catalog.");
        var entries = new List<CatalogEntry>();
        foreach (var node in array)
        {
            if (node is not JsonObject o
                || Text(o, "id") is not { Length: > 0 } id
                || Text(o, "name") is not { Length: > 0 } name
                || !Enum.TryParse<ProviderKind>(Text(o, "kind"), out var kind)
                || !IsHttps(Text(o, "url")) || !IsHttps(Text(o, "page")))
                continue;
            entries.Add(new CatalogEntry(id, name, kind, Text(o, "url")!, Text(o, "page")!,
                new StatusFilter(List(o, "include"), List(o, "exclude"), List(o, "regions")), custom));
        }
        return entries;
    }

    public static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0;

    private static IReadOnlyList<CatalogEntry> LoadEmbedded()
    {
        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream("ServiceStatus.NockMon.Catalog.json")
                           ?? throw new InvalidOperationException("The built-in catalog is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), custom: false);
    }

    private static string? Text(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;

    private static IReadOnlyList<string> List(JsonObject o, string name) =>
        o[name] is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : [];
}
