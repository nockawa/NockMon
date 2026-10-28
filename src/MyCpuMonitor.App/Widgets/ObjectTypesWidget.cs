using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Sampling;
using MyCpuMonitor.Monitoring;

namespace MyCpuMonitor.Widgets;

/// <summary>
/// Kernel objects per type (scan rate). Objects far above handles are referenced by the kernel itself; sustained
/// growth of a type is a leak (this is how a token/job/silo leak shows up). Header click sorts.
/// </summary>
public sealed partial class ObjectTypesWidget : UserControl, IMonitorWidget
{
    private static readonly (ObjectTypeField Field, CellColumn Column)[] Columns =
    [
        (ObjectTypeField.Name, new("Type", "Kernel object type", 150, RightAligned: false)),
        (ObjectTypeField.Objects, new("Objects", "Live objects", 84)),
        (ObjectTypeField.Handles, new("Handles", "Open handles to objects of this type", 84)),
        (ObjectTypeField.ObjectGrowth, new("Obj/min", "Object growth per minute since the previous scan", 72)),
        (ObjectTypeField.HandleGrowth, new("Hdl/min", "Handle growth per minute since the previous scan", 72)),
        (ObjectTypeField.PeakObjects, new("Peak obj", "Highest object count since boot", 84)),
        (ObjectTypeField.PeakHandles, new("Peak hdl", "Highest handle count since boot", 84)),
    ];

    private readonly MonitorService _monitor;
    private readonly ObjectTypesWidgetConfig _config;
    private readonly ObjectTypeQuery _query = new();
    private readonly CellTable _table;
    private long _lastScanIndex = -1;
    private bool _dirty = true;

    public ObjectTypesWidget(ObjectTypesWidgetConfig config, MonitorService monitor)
    {
        _config = config;
        _monitor = monitor;
        _table = new CellTable(Columns.Select(c => c.Column).ToArray(), config.TopCount);
        _table.HeaderTapped += column => SortBy(Columns[column].Field);
        Content = _table;
        UpdateSortIndicator();
    }

    public event Action? ConfigChanged;

    public void Refresh()
    {
        var ran = _monitor.Engine.Read(this, static (w, snapshot, _) =>
        {
            if (snapshot.Table.ScanIndex == w._lastScanIndex && !w._dirty)
                return false;
            w._lastScanIndex = snapshot.Table.ScanIndex;
            w._query.Run(snapshot.Table, w._config.SortBy, w._config.Descending, w._config.TopCount);
            return true;
        });
        if (!ran)
            return;
        _dirty = false;

        var c = CultureInfo.CurrentCulture;
        var rows = _query.Results;
        for (var r = 0; r < rows.Length && r < _table.RowCount; r++)
        {
            ref readonly var t = ref rows[r];
            _table.SetCell(r, 0, t.Name);
            _table.SetCell(r, 1, t.Objects.ToString("#,0", c));
            _table.SetCell(r, 2, t.Handles.ToString("#,0", c));
            _table.SetCell(r, 3, Growth(t.ObjectGrowth, c));
            _table.SetCell(r, 4, Growth(t.HandleGrowth, c));
            _table.SetCell(r, 5, t.PeakObjects.ToString("#,0", c));
            _table.SetCell(r, 6, t.PeakHandles.ToString("#,0", c));
        }
        _table.ClearFrom(rows.Length);
    }

    private static string Growth(double perMinute, CultureInfo c) =>
        Math.Abs(perMinute) < 0.5 ? "0" : perMinute.ToString("+#,0;-#,0", c);

    private void SortBy(ObjectTypeField field)
    {
        if (_config.SortBy == field)
            _config.Descending = !_config.Descending;
        else
        {
            _config.SortBy = field;
            _config.Descending = field != ObjectTypeField.Name;
        }
        UpdateSortIndicator();
        _dirty = true;
        Refresh();
        ConfigChanged?.Invoke();
    }

    private void UpdateSortIndicator() =>
        _table.SetSortIndicator(Array.FindIndex(Columns, c => c.Field == _config.SortBy), _config.Descending);
}
