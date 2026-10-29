using System.Text.Json.Serialization;
using NockMon.Core.Metrics;
using NockMon.Core.Sampling;

namespace NockMon.Core.Settings;

/// <summary>
/// Restored (non-maximized) window rectangle in physical pixels, workspace coordinates (Win32 WINDOWPLACEMENT),
/// and whether the window was maximized.
/// </summary>
public sealed class WindowPlacementSettings
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public bool Maximized { get; set; }

    [JsonIgnore] public bool IsValid => Right - Left >= 100 && Bottom - Top >= 100;
}

public sealed class AppSettings
{
    /// <summary>Fast rate: system, per-CPU and watched processes.</summary>
    public int SampleIntervalMs { get; set; } = 500;

    /// <summary>Full process scan rate (top-N tables, per-name history). Rounded to a multiple of the sample interval.</summary>
    public int ScanIntervalMs { get; set; } = 2000;

    public int HistoryMinutes { get; set; } = 60;
    public CpuScale CpuScale { get; set; } = CpuScale.Machine;
    public bool AlwaysOnTop { get; set; }

    /// <summary>The window's close button hides it to the notification area; Exit is in the tray menu.</summary>
    public bool CloseToTray { get; set; } = true;
    public string ActiveLayout { get; set; } = "Default";

    /// <summary>Where the Plugins dialog looks for plugins: NuGet V3 feeds (URLs) or folders of .nupkg files.</summary>
    public List<string> PluginSources { get; set; } = [Plugins.PluginFeeds.NuGetOrg];

    /// <summary>Show prerelease versions of plugins.</summary>
    public bool PluginPrerelease { get; set; }

    /// <summary>Plugin build output folders loaded at start (plugin development), in addition to installed plugins.</summary>
    public List<string> DevPluginFolders { get; set; } = [];

    /// <summary>Main window placement at last close; null = default size.</summary>
    public WindowPlacementSettings? Window { get; set; }

    /// <summary>Sampling options from these settings, clamped to valid values.</summary>
    [JsonIgnore]
    public SamplingOptions SamplingOptions
    {
        get
        {
            var interval = TimeSpan.FromMilliseconds(Math.Max(SampleIntervalMs, SamplingOptions.MinInterval.TotalMilliseconds));
            var scan = TimeSpan.FromMilliseconds(Math.Max(ScanIntervalMs, interval.TotalMilliseconds));
            var retention = TimeSpan.FromMinutes(Math.Max(HistoryMinutes, 1));
            return new SamplingOptions(interval, scan, retention < scan * 2 ? scan * 2 : retention);
        }
    }
}
