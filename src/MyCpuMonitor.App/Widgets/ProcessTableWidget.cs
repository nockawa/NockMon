using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Sampling;
using MyCpuMonitor.Monitoring;

namespace MyCpuMonitor.Widgets;

/// <summary>
/// Top-N process table with configurable columns. Refreshed when a new scan arrives (or sort/display changes).
/// Clicking a header sorts by that column (again: reverses the order).
/// </summary>
public sealed partial class ProcessTableWidget : UserControl, IMonitorWidget
{
    private readonly MonitorService _monitor;
    private readonly ProcessTableWidgetConfig _config;
    private readonly ProcessQuery _query = new();
    private readonly CellTable _table;
    private long _lastScanIndex = -1;
    private int _lastDisplayVersion = -1;
    private bool _queryDirty = true;

    public ProcessTableWidget(ProcessTableWidgetConfig config, MonitorService monitor)
    {
        _config = config;
        _monitor = monitor;
        var columns = config.Columns
            .Select(c => new CellColumn(MetricCatalog.Get(c.Field).ShortName, MetricCatalog.Get(c.Field).DisplayName, c.Width, IsRightAligned(c.Field),
                Icon: c.Field == ProcessField.Name, Doc: Core.Help.Docs.Of(c.Field)))
            .ToArray();
        _table = new CellTable(columns, config.TopCount);
        _table.HeaderTapped += column => SortBy(_config.Columns[column].Field);
        Content = _table;
        UpdateSortIndicator();
    }

    public event Action? ConfigChanged;

    public void Refresh()
    {
        // The table only changes with a new scan (slower than samples), a sort change or a display setting change.
        var options = new ProcessQueryOptions(_config.SortBy, _config.Descending, _config.TopCount, _config.GroupByName, _config.Filter, _config.ExcludeIdle);
        var ran = _monitor.Engine.Read((widget: this, options), static (state, snapshot, _) =>
        {
            var w = state.widget;
            if (snapshot.Table.ScanIndex == w._lastScanIndex && !w._queryDirty)
                return false;
            w._lastScanIndex = snapshot.Table.ScanIndex;
            w._query.Run(snapshot, state.options);
            return true;
        });
        if (!ran && _monitor.DisplayVersion == _lastDisplayVersion)
            return;
        _queryDirty = false;
        _lastDisplayVersion = _monitor.DisplayVersion;

        // Results are owned by the query: read them outside the engine lock.
        var rows = _query.Results;
        var columns = _config.Columns;
        var iconPixels = (int)Math.Round(16 * (XamlRoot?.RasterizationScale ?? 1));
        for (var r = 0; r < rows.Length && r < _table.RowCount; r++)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                _table.SetCell(r, c, FormatCell(in rows[r], columns[c].Field));
                if (columns[c].Field == ProcessField.Name)
                    _table.SetIcon(r, c, ProcessIcons.Get(rows[r].Stats.Pid, rows[r].Stats.CreateTime, iconPixels));
            }
        }
        _table.ClearFrom(rows.Length);
    }

    private string FormatCell(in ProcessRow row, ProcessField field)
    {
        var grouped = _config.GroupByName && row.Instances > 1;
        return field switch
        {
            ProcessField.Name => row.Stats.Name,
            ProcessField.Pid or ProcessField.ParentPid or ProcessField.SessionId when grouped => "",
            ProcessField.Pid or ProcessField.ParentPid or ProcessField.SessionId or ProcessField.BasePriority =>
                ((long)row.GetValue(field)).ToString(CultureInfo.InvariantCulture),
            ProcessField.StartTime => FormatStart(row.Stats.CreateTime),
            _ => _monitor.Format(row.GetValue(field), MetricCatalog.Get(field).Unit),
        };
    }

    private static string FormatStart(long fileTime)
    {
        if (fileTime <= 0)
            return "";
        var local = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("T", CultureInfo.CurrentCulture) : local.ToString("g", CultureInfo.CurrentCulture);
    }

    private static bool IsRightAligned(ProcessField field) => field is not (ProcessField.Name or ProcessField.StartTime);

    private void SortBy(ProcessField field)
    {
        if (_config.SortBy == field)
            _config.Descending = !_config.Descending;
        else
        {
            _config.SortBy = field;
            _config.Descending = field != ProcessField.Name;
        }
        UpdateSortIndicator();
        _queryDirty = true;
        Refresh();
        ConfigChanged?.Invoke();
    }

    private void UpdateSortIndicator() =>
        _table.SetSortIndicator(_config.Columns.FindIndex(c => c.Field == _config.SortBy), _config.Descending);
}
