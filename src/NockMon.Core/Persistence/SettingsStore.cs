using System.Text.Json;
using NockMon.Core.Settings;

namespace NockMon.Core.Persistence;

public sealed class SettingsStore(string path)
{
    public string Path { get; } = path;

    /// <summary>Missing or corrupt file yields defaults.</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize(File.ReadAllText(Path), MonitorJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (JsonException)
        {
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings) =>
        AppPaths.WriteAtomically(Path, JsonSerializer.Serialize(settings, MonitorJsonContext.Default.AppSettings));
}
