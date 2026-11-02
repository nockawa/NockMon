using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace NockMon.Core.Folders;

/// <summary>
/// One walk of a folder: total size, files, subfolders, and the size of each direct subfolder. Iterative (no recursion
/// depth limit), one directory listing at a time (NtQueryDirectoryFile under .NET's enumerator: sizes come with the
/// listing, no per-file call). Junctions and directory symbolic links are not followed: no loops, no double counting,
/// no other volume. Folders that cannot be listed are counted, not fatal.
/// </summary>
public static partial class FolderScan
{
    private const int LargestCount = 5;

    // Files whose allocation differs from their length: compressed, sparse, and cloud placeholders (OneDrive "online only").
    private const FileAttributes AllocationDiffers = FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.Offline
        | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */ | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */;

    private static readonly EnumerationOptions Listing = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Win32,
    };

    private readonly record struct Entry(bool IsDirectory, long Bytes, string? Path);

    public static FolderScanResult Run(FolderScope scope, CancellationToken cancellation = default)
    {
        var start = Stopwatch.GetTimestamp();
        var root = scope.FullPath;
        if (root.Length == 0)
            return Failed("No folder set.");
        if (!Directory.Exists(root))
            return Failed($"Folder not found: {root}");

        var include = Patterns(scope.Include);
        var exclude = Patterns(scope.Exclude);
        var cluster = scope.SizeOnDisk ? ClusterSize(root) : 0;
        long bytes = 0, files = 0, folders = 0;
        var unreadable = 0;
        var partNames = new List<string>();
        var partBytes = new List<long>();
        var pending = new Stack<(string Path, int Part)>();
        pending.Push((root, -1));

        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var listing = new FileSystemEnumerable<Entry>(current.Path, (ref e) => e.IsDirectory
                    ? new Entry(true, 0, e.ToFullPath())
                    : new Entry(false, cluster > 0 ? Allocated(ref e, cluster) : e.Length, null), Listing)
                {
                    ShouldIncludePredicate = (ref e) => e.IsDirectory
                        ? !Matches(exclude, e.FileName) && !IsLink(ref e)
                        : include.Length == 0 || Matches(include, e.FileName),
                };
                foreach (var entry in listing)
                {
                    if (!entry.IsDirectory)
                    {
                        bytes += entry.Bytes;
                        files++;
                        if (current.Part >= 0)
                            partBytes[current.Part] += entry.Bytes;
                        continue;
                    }
                    folders++;
                    if (!scope.Recursive)
                        continue;
                    var part = current.Part;
                    if (part < 0)
                    {
                        part = partNames.Count;
                        partNames.Add(Path.GetFileName(entry.Path!));
                        partBytes.Add(0);
                    }
                    pending.Push((entry.Path!, part));
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                if (current.Part < 0)
                    return Failed($"Cannot read {root}: {e.Message}");
                unreadable++;
            }
        }

        var largest = Enumerable.Range(0, partNames.Count)
            .OrderByDescending(i => partBytes[i])
            .Take(LargestCount)
            .Select(i => new FolderPart(partNames[i], partBytes[i]))
            .ToArray();
        return new FolderScanResult(DateTime.UtcNow, bytes, files, folders, unreadable, Stopwatch.GetElapsedTime(start), largest);

        FolderScanResult Failed(string error) => new(DateTime.UtcNow, 0, 0, 0, 0, Stopwatch.GetElapsedTime(start), [], error);
    }

    /// <summary>"*.log; *.tmp" → ["*.log", "*.tmp"]; null or blank → none.</summary>
    internal static string[] Patterns(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Matches(string[] patterns, ReadOnlySpan<char> name)
    {
        foreach (var pattern in patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Junction or directory symbolic link (a name surrogate: following it would scan another folder, maybe an ancestor).
    /// Other reparse points (OneDrive and other cloud folders, deduplication) are real folders and are scanned.
    /// </summary>
    private static bool IsLink(ref FileSystemEntry entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
            return false;
        try
        {
            return new DirectoryInfo(entry.ToFullPath()).LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true; // Unreadable reparse point: don't follow.
        }
    }

    /// <summary>Space a file takes on its volume: its allocation (smaller when compressed, sparse or in the cloud), rounded up to clusters.</summary>
    private static long Allocated(ref FileSystemEntry entry, long cluster)
    {
        var size = entry.Length;
        if ((entry.Attributes & AllocationDiffers) != 0)
        {
            var path = entry.ToFullPath();
            if (path.Length >= 260 && !path.StartsWith(@"\\?\", StringComparison.Ordinal)) // Beyond MAX_PATH: the long form.
                path = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
            var low = GetCompressedFileSizeW(path, out var high);
            if (low != uint.MaxValue || Marshal.GetLastPInvokeError() == 0)
                size = ((long)high << 32) | low;
        }
        return (size + cluster - 1) / cluster * cluster;
    }

    private static long ClusterSize(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root is not null && !root.EndsWith('\\'))
            root += '\\';
        return root is not null && GetDiskFreeSpaceW(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _)
            ? (long)sectorsPerCluster * bytesPerSector
            : 4096;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceW(string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint numberOfFreeClusters, out uint totalNumberOfClusters);
}
