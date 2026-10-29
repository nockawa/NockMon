using System.IO.Compression;
using System.Xml.Linq;

namespace MyCpuMonitor.Core.Plugins;

/// <summary>
/// A plugin package (.nupkg, a zip): the nuspec metadata plus the plugin folder under <c>plugin/</c>, whose manifest
/// says what to load. Package type <see cref="PackageType"/> marks it as a plugin for feeds.
/// </summary>
public sealed class PluginPackage
{
    public const string PackageType = "MyCpuMonPlugin";
    private const string PayloadPrefix = "plugin/";

    private PluginPackage(NuspecInfo nuspec, PluginManifest manifest, byte[] content)
    {
        Nuspec = nuspec;
        Manifest = manifest;
        _content = content;
    }

    private readonly byte[] _content;

    public NuspecInfo Nuspec { get; }
    public PluginManifest Manifest { get; }

    /// <exception cref="InvalidDataException">Not a zip, no nuspec, not a plugin, or ids that disagree.</exception>
    public static PluginPackage Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var content = buffer.ToArray();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            var nuspec = ReadNuspec(zip) ?? throw new InvalidDataException("Not a NuGet package (no .nuspec).");
            var entry = zip.GetEntry(PayloadPrefix + PluginManifest.FileName)
                        ?? throw new InvalidDataException($"'{nuspec.Id}' is not a My CPU Monitor plugin (no {PayloadPrefix}{PluginManifest.FileName}).");
            using var reader = new StreamReader(entry.Open());
            var manifest = PluginManifest.Parse(reader.ReadToEnd());
            if (!string.Equals(manifest.Id, nuspec.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Package '{nuspec.Id}' declares plugin '{manifest.Id}'.");
            return new PluginPackage(nuspec, manifest with { Version = nuspec.Version }, content);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            throw new InvalidDataException($"Invalid package: {e.Message}", e);
        }
    }

    /// <summary>Extracts the plugin folder into <paramref name="directory"/> (created; must not exist).</summary>
    public void ExtractTo(string directory)
    {
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        using var zip = new ZipArchive(new MemoryStream(_content), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(PayloadPrefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/'))
                continue;
            // NuGet escapes some characters in part names (e.g. spaces as %20).
            var relative = Uri.UnescapeDataString(entry.FullName[PayloadPrefix.Length..]).Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Invalid path in package: '{entry.FullName}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>The nuspec of a package file, or null if it has none (not a package).</summary>
    public static NuspecInfo? ReadNuspec(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return ReadNuspec(zip);
    }

    private static NuspecInfo? ReadNuspec(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return null;
        using var stream = entry.Open();
        return NuspecInfo.Parse(XDocument.Load(stream));
    }
}

/// <summary>The nuspec fields the plugin UI shows.</summary>
public sealed record NuspecInfo(
    string Id,
    string Version,
    string Title,
    string Description,
    string Authors,
    string? ProjectUrl,
    string? IconUrl,
    IReadOnlyList<string> PackageTypes)
{
    public bool IsPlugin => PackageTypes.Contains(PluginPackage.PackageType, StringComparer.OrdinalIgnoreCase);

    public static NuspecInfo Parse(XDocument document)
    {
        // The namespace depends on the nuspec schema version: match local names.
        var metadata = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")
                       ?? throw new InvalidDataException("Nuspec without metadata.");
        string? Value(string name) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
        var id = Value("id") ?? throw new InvalidDataException("Nuspec without id.");
        var version = Value("version") ?? throw new InvalidDataException("Nuspec without version.");
        var types = metadata.Elements().Where(e => e.Name.LocalName == "packageTypes")
            .SelectMany(e => e.Elements())
            .Select(e => (string?)e.Attribute("name"))
            .OfType<string>()
            .ToList();
        return new NuspecInfo(id, version, Value("title") ?? id, Value("description") ?? "", Value("authors") ?? "",
            Value("projectUrl"), Value("iconUrl"), types);
    }
}
