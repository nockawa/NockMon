using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Versioning;

namespace NockMon.Core.Plugins;

/// <summary>An installed plugin as recorded by the store.</summary>
/// <param name="Version">Version to load at the next start (after an update, newer than the one running).</param>
/// <param name="Source">Feed it came from (shown, and used for updates).</param>
/// <param name="PendingRemoval">Uninstalled: deleted at the next start.</param>
public sealed record InstalledPlugin(string Id, string Version, string Source, bool PendingRemoval, string Directory);

/// <summary>
/// Installed plugins: <c>&lt;root&gt;\&lt;id&gt;\&lt;version&gt;\</c> folders plus <c>state.json</c> recording the version to load
/// and pending removals. Loaded assemblies are locked, so replacing or deleting a plugin takes effect at the next start
/// (<see cref="ApplyPending"/>, before anything is loaded); a new plugin can be loaded right away.
/// </summary>
public sealed class PluginStore(string root, Version hostApi)
{
    private const string StateFile = "state.json";

    public string Root { get; } = root;

    public IReadOnlyList<InstalledPlugin> List() =>
        ReadState().Plugins
            .Select(p => new InstalledPlugin(p.Id, p.Version, p.Source, p.PendingRemoval, VersionDirectory(p.Id, p.Version)))
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public InstalledPlugin? Find(string id) => List().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Installs (or updates to) the package: extracts it into its version folder and makes it the version to load.
    /// Clears a pending removal of the same plugin.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a plugin package, or built for an incompatible plugin API.</exception>
    public PluginManifest Install(Stream package, string source)
    {
        var read = PluginPackage.Read(package);
        var manifest = read.Manifest;
        if (!PluginManifest.IsCompatible(manifest.Api, hostApi))
            throw new InvalidDataException(
                $"'{manifest.Title}' {manifest.Version} needs plugin API {manifest.Api}; this version of NockMon provides {hostApi}. " +
                (manifest.Api > hostApi ? "Update NockMon." : "Look for a newer version of the plugin."));

        var target = VersionDirectory(manifest.Id, manifest.Version);
        if (!File.Exists(Path.Combine(target, PluginManifest.FileName)))
        {
            // Extract beside, then rename: a failed install never leaves a half folder that looks valid.
            var temp = target + ".tmp";
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
            read.ExtractTo(temp);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(temp, target);
        }

        var state = ReadState();
        state.Plugins.RemoveAll(p => string.Equals(p.Id, manifest.Id, StringComparison.OrdinalIgnoreCase));
        state.Plugins.Add(new StateEntry { Id = manifest.Id, Version = Normalize(manifest.Version), Source = source });
        WriteState(state);
        return manifest;
    }

    /// <summary>Marks the plugin for removal at the next start (its files may be in use).</summary>
    public void Uninstall(string id) => Update(id, e => e.PendingRemoval = true);

    public void CancelUninstall(string id) => Update(id, e => e.PendingRemoval = false);

    /// <summary>
    /// Start-up housekeeping, before loading: deletes uninstalled plugins and versions other than the one to load.
    /// Folders that cannot be deleted are kept and retried next time.
    /// </summary>
    public void ApplyPending()
    {
        var state = ReadState();
        var removed = state.Plugins.Where(p => p.PendingRemoval).ToList();
        foreach (var plugin in removed)
        {
            if (TryDelete(PluginDirectory(plugin.Id)))
                state.Plugins.Remove(plugin);
        }
        if (removed.Count > 0)
            WriteState(state);

        if (!Directory.Exists(Root))
            return;
        foreach (var plugin in state.Plugins)
        {
            var directory = PluginDirectory(plugin.Id);
            if (!Directory.Exists(directory))
                continue;
            foreach (var version in Directory.EnumerateDirectories(directory))
            {
                if (!string.Equals(Path.GetFileName(version), Normalize(plugin.Version), StringComparison.OrdinalIgnoreCase))
                    TryDelete(version);
            }
        }
    }

    private void Update(string id, Action<StateEntry> change)
    {
        var state = ReadState();
        var entry = state.Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Plugin '{id}' is not installed.");
        change(entry);
        WriteState(state);
    }

    private string PluginDirectory(string id) => Path.Combine(Root, SafeName(id));

    private string VersionDirectory(string id, string version) => Path.Combine(PluginDirectory(id), SafeName(Normalize(version)));

    /// <summary>Lower-case normalized version ("1.0" and "1.0.0" are the same folder).</summary>
    private static string Normalize(string version) =>
        NuGetVersion.TryParse(version, out var v) ? v.ToNormalizedString().ToLowerInvariant() : version.ToLowerInvariant();

    private static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException($"Invalid plugin name '{name}'.");
        return name;
    }

    private static bool TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private State ReadState()
    {
        var path = Path.Combine(Root, StateFile);
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), PluginJsonContext.Default.State) ?? new State();
        }
        catch (JsonException)
        {
            // Corrupt: start empty; folders stay on disk and a reinstall reuses them.
        }
        return new State();
    }

    private void WriteState(State state) =>
        Persistence.AppPaths.WriteAtomically(Path.Combine(Root, StateFile), JsonSerializer.Serialize(state, PluginJsonContext.Default.State));

    internal sealed class State
    {
        public List<StateEntry> Plugins { get; set; } = [];
    }

    internal sealed class StateEntry
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Source { get; set; } = "";
        public bool PendingRemoval { get; set; }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PluginStore.State))]
internal sealed partial class PluginJsonContext : JsonSerializerContext;
