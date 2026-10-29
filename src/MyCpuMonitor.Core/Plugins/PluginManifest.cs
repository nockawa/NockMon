using System.Text.Json;

namespace MyCpuMonitor.Core.Plugins;

/// <summary>
/// <c>mycpumon-plugin.json</c>, written next to the plugin assembly by the SDK's build targets: identifies the plugin,
/// its entry assembly and the plugin API version it was built against.
/// </summary>
public sealed record PluginManifest(
    string Id,
    string Version,
    string Assembly,
    Version Api,
    string Title,
    string Description,
    string Authors,
    string? ProjectUrl)
{
    public const string FileName = "mycpumon-plugin.json";

    /// <exception cref="InvalidDataException">Missing or malformed fields.</exception>
    public static PluginManifest Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            string Required(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
                    ? s
                    : throw new InvalidDataException($"Plugin manifest: '{name}' is missing.");
            string? Optional(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

            var id = Required("id");
            var assembly = Required("assembly");
            if (assembly.IndexOfAny(['/', '\\']) >= 0 || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Plugin manifest: invalid assembly '{assembly}'.");
            if (!System.Version.TryParse(Required("api"), out var api))
                throw new InvalidDataException("Plugin manifest: invalid 'api' version.");
            return new PluginManifest(id, Required("version"), assembly, api, Optional("title") ?? id, Optional("description") ?? "",
                Optional("authors") ?? "", Optional("projectUrl"));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Plugin manifest: {e.Message}", e);
        }
    }

    /// <summary>The manifest of the plugin folder <paramref name="directory"/>.</summary>
    public static PluginManifest Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
            throw new InvalidDataException($"No {FileName} in '{directory}'. Build the plugin project (it references MyCpuMonitor.Sdk) and use its output folder.");
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// A plugin built against API <paramref name="plugin"/> runs on a host implementing <paramref name="host"/>: same
    /// major (breaking changes), plugin minor not newer (additions it could call).
    /// </summary>
    public static bool IsCompatible(Version plugin, Version host) => plugin.Major == host.Major && plugin.Minor <= host.Minor;
}
