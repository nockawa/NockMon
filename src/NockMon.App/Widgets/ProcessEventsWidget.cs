using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using NockMon.Core.Etw;
using NockMon.Core.Layout;
using NockMon.Core.Model;
using NockMon.Monitoring;

namespace NockMon.Widgets;

/// <summary>Recent process starts and exits (ETW), newest first, with command lines and exit codes.</summary>
public sealed partial class ProcessEventsWidget : UserControl, IMonitorWidget
{
    private static readonly CellColumn[] Columns =
    [
        new("Time", "Local time", 72, RightAligned: false),
        new("Event", "Start or exit", 44, RightAligned: false),
        new("Name", "Image name", 150, RightAligned: false),
        new("PID", "Process ID", 56),
        new("Parent", "Parent process ID", 56),
        new("Exit", "Exit code (hex)", 80, Monospace: true),
        new("Command line", "Command line (starts only)", 600, RightAligned: false),
    ];

    private readonly MonitorService _monitor;
    private readonly ProcessEventsWidgetConfig _config;
    private readonly NamePattern? _filter;
    private readonly CellTable _table;
    private readonly List<ProcessEvent> _events = [];
    private ProcessEvent? _lastNewest;

    public ProcessEventsWidget(ProcessEventsWidgetConfig config, MonitorService monitor)
    {
        _config = config;
        _monitor = monitor;
        _filter = string.IsNullOrWhiteSpace(config.Filter) ? null : new NamePattern(config.Filter);
        _table = new CellTable(Columns, config.MaxRows);
        Content = _table;
    }

    public event Action? ConfigChanged
    {
        add { }
        remove { }
    }

    public void Refresh()
    {
        var source = _monitor.Engine.KernelEvents;
        if (source is not { Status: null })
        {
            _table.SetCell(0, 2, source?.Status ?? "Kernel events are not enabled.");
            return;
        }

        source.CopyRecentEvents(_events);
        var newest = _events.Count > 0 ? _events[^1] : null;
        if (ReferenceEquals(newest, _lastNewest))
            return; // Nothing new.
        _lastNewest = newest;

        var row = 0;
        for (var i = _events.Count - 1; i >= 0 && row < _table.RowCount; i--)
        {
            var e = _events[i];
            if ((e.Kind == ProcessEventKind.Start && !_config.ShowStarts) || (e.Kind == ProcessEventKind.Exit && !_config.ShowExits))
                continue;
            if (_filter is not null && !_filter.IsMatch(e.Name))
                continue;
            _table.SetCell(row, 0, e.TimeUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture));
            _table.SetCell(row, 1, e.Kind == ProcessEventKind.Start ? "start" : "exit");
            _table.SetCell(row, 2, e.Name);
            _table.SetCell(row, 3, e.Pid.ToString(CultureInfo.InvariantCulture));
            _table.SetCell(row, 4, e.ParentPid.ToString(CultureInfo.InvariantCulture));
            _table.SetCell(row, 5, e.ExitCode is { } code ? $"0x{code:X}" : "");
            _table.SetCell(row, 6, e.CommandLine ?? "");
            row++;
        }
        _table.ClearFrom(row);
    }
}
