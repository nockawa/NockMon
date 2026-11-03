using System.Text.Json;
using System.Text.Json.Nodes;

namespace ServiceStatus.NockMon.Status;

/// <summary>
/// A service watched in the background, under the filter of the widget it was ticked in: <see cref="History"/>, its state
/// recorded as a metric.
/// </summary>
public sealed record Watched(string Id, StatusFilter Filter, bool History);

/// <summary>
/// The services watched in the background: plugin-wide, unlike what a widget follows, so in the plugin's folder
/// (watchlist.json), not in a widget's settings. They are read for the whole session, shown by a widget or not.
/// </summary>
public static class Watchlist
{
    private const string File = "watchlist.json";

    /// <summary>The state as a metric value: 0 operational, 1 maintenance, 2 degraded, 3 partial outage, 4 major outage; unknown: NaN.</summary>
    public static double Value(Severity severity) => severity switch
    {
        Severity.Operational => 0,
        Severity.Maintenance => 1,
        Severity.Degraded => 2,
        Severity.PartialOutage => 3,
        Severity.MajorOutage => 4,
        _ => double.NaN,
    };

    /// <summary>A catalog id as a metric id (custom ids hold the page's path).</summary>
    public static string MetricId(string id) => id.Replace('/', '.');

    public static IReadOnlyList<Watched> Load(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, File);
            if (!System.IO.File.Exists(path) || JsonNode.Parse(System.IO.File.ReadAllText(path)) is not JsonArray array)
                return [];
            var list = new List<Watched>();
            foreach (var node in array)
            {
                if (node is not JsonObject o || o["id"] is not JsonValue v || !v.TryGetValue<string>(out var id) || id.Length == 0
                    || list.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var watched = new Watched(id, new StatusFilter(Strings(o["include"]), Strings(o["exclude"]), Strings(o["regions"])), Flag(o["history"]));
                if (watched.History)
                    list.Add(watched);
            }
            return list;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public static void Save(string dataDirectory, IEnumerable<Watched> watched)
    {
        var array = new JsonArray(watched.Where(w => w.History).Select(w => (JsonNode)new JsonObject
        {
            ["id"] = w.Id,
            ["history"] = w.History,
            ["include"] = new JsonArray(w.Filter.Include.Select(s => (JsonNode)s).ToArray()),
            ["exclude"] = new JsonArray(w.Filter.Exclude.Select(s => (JsonNode)s).ToArray()),
            ["regions"] = new JsonArray(w.Filter.Regions.Select(s => (JsonNode)s).ToArray()),
        }).ToArray());
        Directory.CreateDirectory(dataDirectory);
        System.IO.File.WriteAllText(Path.Combine(dataDirectory, File), array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : [];
}
