using System.Security.Principal;
using System.Text;

namespace NockMon.Core.Persistence;

/// <summary>
/// Where NockMon keeps its files: <c>%ProgramData%\NockMon\&lt;user SID&gt;</c>, in a folder only administrators can
/// modify. NockMon runs elevated and loads plugins, settings and layouts from there: in a folder the user's
/// non-elevated processes can write (<c>%LOCALAPPDATA%</c>, before 1.2), any of them could plant code that would then
/// run as administrator, or replace a folder with a junction to redirect NockMon's own writes.
/// </summary>
public static class AppPaths
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// NOCKMON_DATA_DIR (tests, profiling, alternative profiles), honored only where it cannot redirect an elevated
    /// release build: any of the user's processes can set it (HKCU\Environment).
    /// </summary>
    private static readonly string? DataDirOverride =
        OverrideAllowed && Environment.GetEnvironmentVariable("NOCKMON_DATA_DIR") is { Length: > 0 } dir ? dir : null;

#if DEBUG
    private static bool OverrideAllowed => true;
#else
    private static bool OverrideAllowed => !Environment.IsPrivilegedProcess;
#endif

    /// <summary><c>%ProgramData%\NockMon</c>: administrators only, one subfolder per user.</summary>
    public static string MachineRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NockMon");

    /// <summary>The current user's folder in <see cref="MachineRoot"/>, or NOCKMON_DATA_DIR where allowed.</summary>
    public static string Root { get; } = DataDirOverride ?? Path.Combine(MachineRoot, WindowsIdentity.GetCurrent().User!.Value);

    /// <summary>Set by <see cref="Prepare"/> when the user should know something (an untrusted folder moved away).</summary>
    public static string? StartupWarning { get; private set; }

    /// <summary>
    /// At start, elevated, before anything reads the data: secures <see cref="MachineRoot"/>, then copies the data of an
    /// older version in, once (<c>%LOCALAPPDATA%\NockMon</c>, or <c>%LOCALAPPDATA%\MyCpuMonitor</c> from before the
    /// rename). Nothing with NOCKMON_DATA_DIR.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The folder cannot be secured.</exception>
    public static void Prepare()
    {
        if (DataDirOverride is not null)
            return;
        if (SecureFolder.Ensure(MachineRoot) is { } moved)
            StartupWarning = $"{MachineRoot} existed but NockMon did not create it: another program may have prepared it " +
                $"to run code as administrator. It was moved to {moved} and NockMon started with a new one.";
        if (!Directory.Exists(Root))
        {
            foreach (var legacy in new[] { Path.Combine(LocalAppData, "NockMon"), Path.Combine(LocalAppData, "MyCpuMonitor") })
            {
                if (MigrateLegacyRoot(legacy, Root))
                    break;
            }
        }
        Directory.CreateDirectory(Root);
    }

    /// <summary>
    /// Copies <paramref name="legacy"/> to <paramref name="root"/> (which must not exist yet) through a staging folder,
    /// links skipped, then renames <paramref name="legacy"/> "… (moved to ProgramData)".
    /// </summary>
    /// <returns>True if copied.</returns>
    internal static bool MigrateLegacyRoot(string legacy, string root)
    {
        if (!Directory.Exists(legacy) || Directory.Exists(root) || IsLink(legacy))
            return false;
        var staging = root + ".migrating";
        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true); // A previous attempt interrupted.
            CopyTree(legacy, staging);
            Directory.Move(staging, root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // Starts fresh; the old folder stays for the next start.
        }
        try
        {
            Directory.Move(legacy, legacy + " (moved to ProgramData)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left in place: never read again (the new folder exists).
        }
        return true;
    }

    private static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Links (junctions, symbolic links) are skipped: one could pull any folder of the machine in.</summary>
    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            var target = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo)
                CopyTree(entry.FullName, target);
            else
                ((FileInfo)entry).CopyTo(target);
        }
    }

    public static string Layouts => Path.Combine(Root, "Layouts");
    public static string Settings => Path.Combine(Root, "settings.json");

    /// <summary>Write to a temp file, flushed to disk, then rename: a crash or a power loss never leaves a truncated file.</summary>
    internal static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(content));
            stream.Flush(flushToDisk: true);
        }
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }
}
