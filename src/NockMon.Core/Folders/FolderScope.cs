using NockMon.Core.Metrics;

namespace NockMon.Core.Folders;

/// <summary>What a folder scan covers. Two series or widgets with the same scope share one scan and one history.</summary>
/// <param name="Path">Folder path; environment variables (<c>%TEMP%</c>) are expanded.</param>
/// <param name="Recursive">Subfolders included.</param>
/// <param name="Include">File name patterns (<c>*.log;*.tmp</c>); null = every file.</param>
/// <param name="Exclude">Folder name patterns skipped with their content (<c>node_modules;.git</c>); null = none.</param>
/// <param name="SizeOnDisk">Allocated size (clusters, compression, sparse and cloud files) instead of the logical size.</param>
public sealed record FolderScope(string Path, bool Recursive = true, string? Include = null, string? Exclude = null, bool SizeOnDisk = false)
{
    /// <summary>The folder's own name ("Downloads"), or the path for a root ("D:\").</summary>
    public string Name
    {
        get
        {
            var trimmed = Path.Trim().TrimEnd('\\', '/');
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? Path.Trim() : name;
        }
    }

    /// <summary>The path to scan: trimmed, environment variables expanded.</summary>
    public string FullPath => Environment.ExpandEnvironmentVariables(Path.Trim());

    public static MetricInfo Info(FolderMetric metric) => metric switch
    {
        FolderMetric.Size => new("Folder size", "Size", MetricUnit.Bytes, true),
        FolderMetric.Files => new("Folder files", "Files", MetricUnit.Count, true),
        FolderMetric.Folders => new("Folder subfolders", "Folders", MetricUnit.Count, true),
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
    };

    /// <summary>Same folder: paths equal ignoring case, surrounding spaces and a trailing separator.</summary>
    public bool IsPath(string path) =>
        string.Equals(Path.Trim().TrimEnd('\\', '/'), path.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>A subfolder (directly under the scanned folder) and its total size.</summary>
public readonly record struct FolderPart(string Name, long Bytes);

/// <summary>Result of one scan. <see cref="Error"/> set: the counts are not meaningful.</summary>
/// <param name="Unreadable">Folders whose content could not be listed (access denied, removed during the scan).</param>
/// <param name="Largest">Largest subfolders, biggest first (recursive scans only).</param>
public sealed record FolderScanResult(
    DateTime TimeUtc,
    long Bytes,
    long Files,
    long Folders,
    int Unreadable,
    TimeSpan Duration,
    IReadOnlyList<FolderPart> Largest,
    string? Error = null)
{
    public double Value(FolderMetric metric) => metric switch
    {
        FolderMetric.Size => Bytes,
        FolderMetric.Files => Files,
        _ => Folders,
    };
}

/// <summary>The latest valid counts of a watched scope (what the history records at each sample).</summary>
public readonly record struct FolderReading(FolderScope Scope, FolderScanResult Result);
