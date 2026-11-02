using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
/// Thread-safe: changes are serialized (installs run in the background, several at once).
/// </summary>
public sealed partial class PluginStore(string root, Version hostApi)
{
    private const string StateFile = "state.json";
    private readonly Lock _gate = new();

    public string Root { get; } = root;

    public IReadOnlyList<InstalledPlugin> List()
    {
        lock (_gate)
        {
            return ReadState().Plugins
                .Select(p => new InstalledPlugin(p.Id, p.Version, p.Source, p.PendingRemoval, VersionDirectory(p.Id, p.Version)))
                .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public InstalledPlugin? Find(string id) => List().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Installs (or updates to) the package: extracts it into its version folder and makes it the version to load.
    /// Clears a pending removal of the same plugin.
    /// </summary>
    /// <param name="expectedId">The id the package was requested as: a package claiming another id is refused (it would
    /// replace that other plugin).</param>
    /// <param name="expectedVersion">Likewise, the version requested.</param>
    /// <exception cref="InvalidDataException">Not a plugin package, not the one requested, or built for an incompatible plugin API.</exception>
    public PluginManifest Install(Stream package, string source, string? expectedId = null, string? expectedVersion = null)
    {
        var read = PluginPackage.Read(package);
        var manifest = read.Manifest;
        if (!IsValidId(manifest.Id) || !NuGetVersion.TryParse(manifest.Version, out _))
            throw new InvalidDataException($"Invalid plugin id or version: '{manifest.Id}' {manifest.Version}.");
        if ((expectedId is not null && !string.Equals(manifest.Id, expectedId, StringComparison.OrdinalIgnoreCase)) ||
            (expectedVersion is not null && Normalize(manifest.Version) != Normalize(expectedVersion)))
            throw new InvalidDataException(
                $"The feed returned '{manifest.Id}' {manifest.Version} for '{expectedId}' {expectedVersion}: refused.");
        if (!PluginManifest.IsCompatible(manifest.Api, hostApi))
            throw new InvalidDataException(
                $"'{manifest.Title}' {manifest.Version} needs plugin API {manifest.Api}; this version of NockMon provides {hostApi}. " +
                (manifest.Api > hostApi ? "Update NockMon." : "Look for a newer version of the plugin."));

        lock (_gate)
        {
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
        lock (_gate)
            ApplyPendingLocked();
    }

    private void ApplyPendingLocked()
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
        lock (_gate)
        {
            var state = ReadState();
            var entry = state.Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Plugin '{id}' is not installed.");
            change(entry);
            WriteState(state);
        }
    }

    private string PluginDirectory(string id) => Path.Combine(Root, SafeName(id));

    private string VersionDirectory(string id, string version) => Path.Combine(PluginDirectory(id), SafeName(Normalize(version)));

    /// <summary>Lower-case normalized version ("1.0" and "1.0.0" are the same folder).</summary>
    private static string Normalize(string version) =>
        NuGetVersion.TryParse(version, out var v) ? v.ToNormalizedString().ToLowerInvariant() : version.ToLowerInvariant();

    /// <summary>
    /// A NuGet package id: letters, digits and underscores, in parts joined by single dots or dashes. Nothing Windows
    /// would alias ("a." and "a" are the same folder) or resolve elsewhere ("..").
    /// </summary>
    internal static bool IsValidId(string? id) => id is { Length: > 0 and <= 100 } && IdPattern().IsMatch(id);

    [GeneratedRegex(@"^\w+([.-]\w+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    private static string SafeName(string name)
    {
        // Ids are checked at install and on read; normalized versions parse, so they hold digits, letters, dots, dashes.
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith('.') || name.EndsWith(' '))
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

    /// <summary>Entries that are not a valid id and version (a damaged or edited file) are dropped: never a crash at start.</summary>
    private State ReadState()
    {
        var path = Path.Combine(Root, StateFile);
        State? state = null;
        try
        {
            if (File.Exists(path))
                state = JsonSerializer.Deserialize(File.ReadAllText(path), PluginJsonContext.Default.State);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable: start empty; folders stay on disk and a reinstall reuses them.
        }
        if (state?.Plugins is not { } plugins)
            return new State();
        plugins.RemoveAll(p => p is null || !IsValidId(p.Id) || !NuGetVersion.TryParse(p.Version, out _));
        foreach (var plugin in plugins)
            plugin.Source ??= "";
        return state;
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
