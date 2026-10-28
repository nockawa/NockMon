namespace MyCpuMonitor.Widgets;

/// <summary>Widget content fed by the monitor. All members are called on the UI thread.</summary>
public interface IMonitorWidget
{
    /// <summary>Pull the latest data and update the visuals.</summary>
    void Refresh();

    /// <summary>Raised when the widget changed its own configuration (e.g. table sorted by a header click).</summary>
    event Action? ConfigChanged;
}
