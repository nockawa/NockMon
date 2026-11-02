using System.Text.Json;
using NockMon.Core.Layout;

namespace NockMon.Core.Persistence;

/// <summary>
/// Named layouts, one JSON file each. The name stored in the file is authoritative; the file name is a
/// sanitized form of it, and two names sanitizing to the same file are rejected.
/// </summary>
public sealed class LayoutStore(string directory)
{
    public string Directory { get; } = directory;

    public static string Serialize(LayoutDocument layout) =>
        JsonSerializer.Serialize(layout, MonitorJsonContext.Default.LayoutDocument);

    public static LayoutDocument Deserialize(string json)
    {
        var layout = JsonSerializer.Deserialize(json, MonitorJsonContext.Default.LayoutDocument)
                     ?? throw new InvalidDataException("Empty layout document.");
        if (layout.SchemaVersion > LayoutDocument.CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Layout '{layout.Name}' has schema version {layout.SchemaVersion}; this build supports up to {LayoutDocument.CurrentSchemaVersion}.");
        LayoutRepair.Repair(layout);
        return layout;
    }

    /// <summary>Deep copy through JSON (same rules as persistence), for edit-then-apply dialogs.</summary>
    public static T Clone<T>(T widget) where T : WidgetConfig =>
        (T)JsonSerializer.Deserialize(JsonSerializer.Serialize<WidgetConfig>(widget, MonitorJsonContext.Default.WidgetConfig),
            MonitorJsonContext.Default.WidgetConfig)!;

    /// <summary>A widget in the layout file format (camelCase, enums as names, "$type" discriminator).</summary>
    public static string SerializeWidget(WidgetConfig widget) => JsonSerializer.Serialize(widget, MonitorJsonContext.Default.WidgetConfig);

    /// <exception cref="JsonException">Invalid JSON or unknown "$type".</exception>
    public static WidgetConfig DeserializeWidget(string json) =>
        JsonSerializer.Deserialize(json, MonitorJsonContext.Default.WidgetConfig) ?? throw new JsonException("Empty widget.");

    /// <summary>Names of the readable layouts, sorted. Unreadable files are skipped.</summary>
    public IReadOnlyList<string> List() => LoadAll().Select(l => l.Name).ToList();

    /// <summary>The readable layouts, sorted by name, each read once. Unreadable files are skipped.</summary>
    public IReadOnlyList<LayoutDocument> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];
        var layouts = new List<LayoutDocument>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                layouts.Add(Deserialize(File.ReadAllText(file)));
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or NotSupportedException or IOException)
            {
                // Corrupt or newer file: not listed, left untouched.
            }
        }
        layouts.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return layouts;
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    public LayoutDocument Load(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Layout '{name}' not found.", path);
        return Deserialize(File.ReadAllText(path));
    }

    public void Save(LayoutDocument layout)
    {
        var path = PathFor(layout.Name);
        if (File.Exists(path))
        {
            var existing = TryReadName(path);
            if (existing is not null && !string.Equals(existing, layout.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Layout name '{layout.Name}' conflicts with existing layout '{existing}'.");
        }
        AppPaths.WriteAtomically(path, Serialize(layout));
    }

    public bool Delete(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    public void Rename(string oldName, string newName)
    {
        var layout = Load(oldName);
        if (!string.Equals(FileNameFor(oldName), FileNameFor(newName), StringComparison.OrdinalIgnoreCase) && Exists(newName))
            throw new InvalidOperationException($"Layout '{newName}' already exists.");
        layout.Name = newName;
        if (string.Equals(FileNameFor(oldName), FileNameFor(newName), StringComparison.OrdinalIgnoreCase))
        {
            AppPaths.WriteAtomically(PathFor(newName), Serialize(layout)); // Same file (a case change): rewritten in place.
            return;
        }
        Save(layout); // The new file first: a failure leaves the old one, never neither.
        Delete(oldName);
    }

    public static string FileNameFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Layout name cannot be empty.", nameof(name));
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars) + ".json";
    }

    private string PathFor(string name) => Path.Combine(Directory, FileNameFor(name));

    private static string? TryReadName(string path)
    {
        try
        {
            return Deserialize(File.ReadAllText(path)).Name;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }
}
