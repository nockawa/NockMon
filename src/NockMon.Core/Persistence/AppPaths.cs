namespace NockMon.Core.Persistence;

public static class AppPaths
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string? DataDirOverride = Environment.GetEnvironmentVariable("NOCKMON_DATA_DIR") is { Length: > 0 } dir ? dir : null;

    /// <summary>%LOCALAPPDATA%\NockMon, or NOCKMON_DATA_DIR when set (tests, profiling with specific layouts).</summary>
    public static string Root { get; } = DataDirOverride ?? Path.Combine(LocalAppData, "NockMon");

    /// <summary>
    /// The app was named "My CPU Monitor": moves its data folder (%LOCALAPPDATA%\MyCpuMonitor) to <see cref="Root"/>
    /// once, at start, before anything reads it. Not with NOCKMON_DATA_DIR (a test or profiling folder).
    /// </summary>
    public static void MigrateLegacyRoot()
    {
        if (DataDirOverride is null)
            MigrateLegacyRoot(Path.Combine(LocalAppData, "MyCpuMonitor"), Root);
    }

    /// <returns>True if <paramref name="legacy"/> was moved to <paramref name="root"/> (which must not exist yet).</returns>
    internal static bool MigrateLegacyRoot(string legacy, string root)
    {
        if (!Directory.Exists(legacy) || Directory.Exists(root))
            return false;
        try
        {
            Directory.Move(legacy, root);
            return true;
        }
        catch (IOException)
        {
            return false; // A file in use (the old app still running): start fresh, the old folder stays.
        }
    }

    public static string Layouts => Path.Combine(Root, "Layouts");
    public static string Settings => Path.Combine(Root, "settings.json");

    /// <summary>Write to a temp file then rename: a crash never leaves a truncated file.</summary>
    internal static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
