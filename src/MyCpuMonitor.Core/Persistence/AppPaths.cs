namespace MyCpuMonitor.Core.Persistence;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyCpuMonitor");

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
