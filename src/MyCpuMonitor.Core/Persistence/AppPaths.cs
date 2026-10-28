namespace MyCpuMonitor.Core.Persistence;

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\MyCpuMonitor, or MYCPUMON_DATA_DIR when set (tests, profiling with specific layouts).</summary>
    public static string Root { get; } = Environment.GetEnvironmentVariable("MYCPUMON_DATA_DIR") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyCpuMonitor");

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
