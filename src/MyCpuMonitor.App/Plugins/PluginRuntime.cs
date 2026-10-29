using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Persistence;
using MyCpuMonitor.Core.Plugins;
using MyCpuMonitor.Sdk;

namespace MyCpuMonitor.Plugins;

/// <summary>A plugin folder this session tried to load.</summary>
internal sealed class LoadedPlugin
{
    public required string Directory { get; init; }

    /// <summary>Development folder (loaded from a copy), null for installed plugins.</summary>
    public string? DevelopmentFolder { get; init; }

    public PluginManifest? Manifest { get; init; }
    public List<IWidgetPlugin> Widgets { get; } = [];

    /// <summary>Why it failed to load (fully or partly); null when fine.</summary>
    public string? Error { get; set; }

    public string Title => Manifest?.Title ?? Path.GetFileName(DevelopmentFolder ?? Directory);
}

/// <summary>
/// Loads plugins (installed ones, development folders, <c>--plugin &lt;folder&gt;</c> arguments) into one load context
/// each, registers their widgets, and installs from feeds. Assemblies the host has (the SDK, WinUI, the runtime…)
/// always resolve to the host's copy: plugins share its types. Nothing can be unloaded: updates, removals and reloads
/// need a restart, which <see cref="RestartRequired"/> tracks. UI thread only.
/// </summary>
internal static class PluginRuntime
{
    private static readonly List<LoadedPlugin> _loaded = [];
    private static readonly Dictionary<(string Plugin, string Widget), IWidgetPlugin> _widgets = [];

    public static PluginStore Store { get; } = new(Path.Combine(AppPaths.Root, "Plugins"), PluginApi.Version);

    public static IReadOnlyList<LoadedPlugin> Loaded => _loaded;

    /// <summary>Folders given with <c>--plugin</c> (development, not saved).</summary>
    public static IReadOnlyList<string> CommandLineFolders { get; private set; } = [];

    private static bool _reloadPending;

    /// <summary>
    /// A change made this session takes effect at the next start: an uninstall, an installed version other than the
    /// running one, or a development folder to reload.
    /// </summary>
    public static bool RestartRequired =>
        _reloadPending || Store.List().Any(p => p.PendingRemoval || FindLoaded(p.Id) is { DevelopmentFolder: null, Manifest: { } m } && !SameVersion(m.Version, p.Version));

    public static void MarkRestartRequired() => _reloadPending = true;

    private static bool SameVersion(string a, string b) =>
        NuGet.Versioning.NuGetVersion.TryParse(a, out var x) && NuGet.Versioning.NuGetVersion.TryParse(b, out var y) ? x == y : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Before Http, which uses it (static initializers run in declaration order).
    public static Version HostVersion { get; } = typeof(PluginRuntime).Assembly.GetName().Version ?? new Version(0, 0);

    public static HttpClient Http { get; } = CreateHttp();

    public static string DataDirectory(string pluginId) => Path.Combine(AppPaths.Root, "PluginData", pluginId);

    /// <summary>Start-up: applies pending removals, then loads development folders first (they override an installed copy).</summary>
    public static void Initialize(IEnumerable<string> developmentFolders, string[] args)
    {
        var commandLine = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--plugin")
                commandLine.Add(Path.GetFullPath(args[++i]));
        }
        CommandLineFolders = commandLine;

        try
        {
            Store.ApplyPending();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Plugin housekeeping failed: {e.Message}");
        }
        CleanDevelopmentCopies();

        foreach (var folder in commandLine.Concat(developmentFolders).Distinct(StringComparer.OrdinalIgnoreCase))
            LoadDevelopment(folder);
        foreach (var plugin in Store.List().Where(p => !p.PendingRemoval))
        {
            if (FindLoaded(plugin.Id) is { } other)
            {
                _loaded.Add(new LoadedPlugin { Directory = plugin.Directory, Error = $"Not loaded: the development folder {other.DevelopmentFolder} provides it." });
                continue;
            }
            Load(plugin.Directory, developmentFolder: null);
        }
    }

    public static IWidgetPlugin? Find(string plugin, string widget) =>
        _widgets.FirstOrDefault(w => string.Equals(w.Key.Plugin, plugin, StringComparison.OrdinalIgnoreCase) && w.Key.Widget == widget).Value;

    public static LoadedPlugin? FindLoaded(string id) =>
        _loaded.FirstOrDefault(p => p.Error is null or "" && string.Equals(p.Manifest?.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? _loaded.FirstOrDefault(p => string.Equals(p.Manifest?.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Downloads and installs <paramref name="listing"/>. A plugin not running yet is loaded at once; a new version of a
    /// running one waits for a restart.
    /// </summary>
    /// <returns>true if its widgets are usable now.</returns>
    public static async Task<bool> InstallAsync(PluginListing listing, CancellationToken cancellation)
    {
        await using var package = await listing.Feed.DownloadAsync(listing.Id, listing.Version, cancellation);
        var manifest = await Task.Run(() => Store.Install(package, listing.Feed.Name), cancellation);
        if (FindLoaded(manifest.Id) is { } running)
        {
            // Same files already running (reinstall after an uninstall) need nothing; anything else waits.
            if (running.DevelopmentFolder is not null || running.Error is not null)
                _reloadPending = true; // Version changes are tracked by RestartRequired itself.
            return false;
        }
        var loaded = Load(Store.Find(manifest.Id)!.Directory, developmentFolder: null);
        return loaded.Error is null && loaded.Widgets.Count > 0;
    }

    /// <summary>Loads a development folder added from the UI. Already loaded plugin: reloaded at the next start.</summary>
    public static LoadedPlugin LoadDevelopment(string folder)
    {
        PluginManifest manifest;
        try
        {
            manifest = PluginManifest.Load(folder);
        }
        catch (InvalidDataException e)
        {
            var failed = new LoadedPlugin { Directory = folder, DevelopmentFolder = folder, Error = e.Message };
            _loaded.Add(failed);
            return failed;
        }
        if (FindLoaded(manifest.Id) is { } running)
        {
            _reloadPending = true;
            return running;
        }

        // Load from a copy: the build can overwrite the output folder while the app runs.
        var copy = Path.Combine(Path.GetTempPath(), "MyCpuMonitor", "DevPlugins", $"{manifest.Id}-{Environment.ProcessId}-{Environment.TickCount64}");
        try
        {
            CopyDirectory(folder, copy);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var failed = new LoadedPlugin { Directory = folder, DevelopmentFolder = folder, Manifest = manifest, Error = $"Cannot copy the folder: {e.Message}" };
            _loaded.Add(failed);
            return failed;
        }
        return Load(copy, folder);
    }

    private static LoadedPlugin Load(string directory, string? developmentFolder)
    {
        PluginManifest manifest;
        try
        {
            manifest = PluginManifest.Load(directory);
        }
        catch (InvalidDataException e)
        {
            var failed = new LoadedPlugin { Directory = directory, DevelopmentFolder = developmentFolder, Error = e.Message };
            _loaded.Add(failed);
            return failed;
        }

        var plugin = new LoadedPlugin { Directory = directory, DevelopmentFolder = developmentFolder, Manifest = manifest };
        _loaded.Add(plugin);
        if (!PluginManifest.IsCompatible(manifest.Api, PluginApi.Version))
        {
            plugin.Error = $"Built for plugin API {manifest.Api}; this version of My CPU Monitor provides {PluginApi.Version}.";
            return plugin;
        }

        try
        {
            var context = new PluginLoadContext(Path.Combine(directory, manifest.Assembly), manifest.Id);
            var assembly = context.LoadFromAssemblyPath(Path.Combine(directory, manifest.Assembly));
            var errors = new List<string>();
            foreach (var type in ExportedTypes(assembly, errors))
            {
                if (!type.IsClass || type.IsAbstract || !typeof(IWidgetPlugin).IsAssignableFrom(type))
                    continue;
                try
                {
                    var widget = (IWidgetPlugin)Activator.CreateInstance(type)!;
                    Register(plugin, widget);
                }
                catch (Exception e)
                {
                    errors.Add($"{type.Name}: {(e as TargetInvocationException)?.InnerException?.Message ?? e.Message}");
                }
            }
            if (plugin.Widgets.Count == 0 && errors.Count == 0)
                errors.Add($"No public {nameof(IWidgetPlugin)} implementation in {manifest.Assembly}.");
            plugin.Error = errors.Count > 0 ? string.Join(" ", errors) : null;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or FileLoadException)
        {
            plugin.Error = $"Cannot load {manifest.Assembly}: {e.Message}";
        }
        return plugin;
    }

    private static IEnumerable<Type> ExportedTypes(Assembly assembly, List<string> errors)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            errors.Add(e.LoaderExceptions.FirstOrDefault()?.Message ?? e.Message);
            return e.Types.OfType<Type>();
        }
    }

    private static void Register(LoadedPlugin plugin, IWidgetPlugin widget)
    {
        var manifest = plugin.Manifest!;
        static GridSize Size(WidgetSize s, int columns, int rows) => new(Math.Max(1, s.Columns > 0 ? s.Columns : columns), Math.Max(1, s.Rows > 0 ? s.Rows : rows));
        var min = Size(widget.MinSize, 1, 1);
        var size = Size(widget.DefaultSize, 4, 3);
        PluginCatalog.Register(new PluginWidgetInfo(manifest.Id, widget.Id, widget.DisplayName, widget.Summary, widget.Details,
            new GridSize(Math.Max(size.Columns, min.Columns), Math.Max(size.Rows, min.Rows)), min,
            manifest.Title, manifest.Version, manifest.Authors));
        _widgets[(manifest.Id, widget.Id)] = widget;
        plugin.Widgets.Add(widget);
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>Copies left by previous sessions (locked while they ran).</summary>
    private static void CleanDevelopmentCopies()
    {
        var root = Path.Combine(Path.GetTempPath(), "MyCpuMonitor", "DevPlugins");
        if (!System.IO.Directory.Exists(root))
            return;
        foreach (var directory in System.IO.Directory.EnumerateDirectories(root))
        {
            try
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still in use (another instance during a restart): next time.
            }
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"MyCpuMonitor/{HostVersion.ToString(3)}");
        return http;
    }

    /// <summary>
    /// One per plugin. Host assemblies (trusted platform assemblies: the runtime, WinUI, the SDK…) resolve to the host's
    /// copy so types are shared; the plugin's own dependencies resolve from its folder (deps.json, then by file name).
    /// </summary>
    private sealed class PluginLoadContext(string mainAssembly, string name) : AssemblyLoadContext($"Plugin {name}")
    {
        private static readonly HashSet<string> HostAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);
        private readonly string _directory = Path.GetDirectoryName(mainAssembly)!;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null || HostAssemblies.Contains(assemblyName.Name))
                return null;
            var path = _resolver.ResolveAssemblyToPath(assemblyName) ?? Path.Combine(_directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : 0;
    }
}
