using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NockMon.Core.Layout;
using NockMon.Core.Metrics;
using NockMon.Core.Model;
using NockMon.Core.Sampling;
using NockMon.Monitoring;

namespace NockMon.Widgets;

/// <summary>
/// Top-N process table with configurable columns. Refreshed when a new scan arrives (or sort/display changes).
/// Clicking a header sorts by that column (again: reverses the order). Right-clicking a row opens the process menu
/// (<see cref="ProcessMenu"/>); the table holds still while it is open.
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
    private bool _menuOpen;

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
        _table.RowRightTapped += OnRowRightTapped;
        Content = _table;
        UpdateSortIndicator();
    }

    public event Action? ConfigChanged;

    public void Refresh()
    {
        if (_menuOpen)
            return; // The rows stay where the menu's row is.
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

    private void OnRowRightTapped(int row, FrameworkElement anchor, Windows.Foundation.Point position)
    {
        var rows = _query.Results;
        if (row >= rows.Length || XamlRoot is null)
            return;
        var stats = rows[row].Stats;
        var target = _config.GroupByName && rows[row].Instances > 1
            ? new ProcessTarget(stats.Name, _monitor.Engine.Read(stats.Name, static (name, snapshot, _) =>
            {
                var keys = new List<ProcessKey>();
                foreach (ref readonly var p in snapshot.ProcessSpan)
                {
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                        keys.Add(new ProcessKey(p.Pid, p.CreateTime));
                }
                return keys;
            }))
            : new ProcessTarget(stats.Name, [new ProcessKey(stats.Pid, stats.CreateTime)]);
        if (target.Instances.Count == 0)
            return; // All exited since the last scan.

        var menu = ProcessMenu.Create(target, _monitor, XamlRoot, () => RowText(row));
        _menuOpen = true;
        _table.HighlightRow(row);
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            _table.HighlightRow(null);
            Refresh();
        };
        menu.ShowAt(anchor, new FlyoutShowOptions { Position = position });
    }

    /// <summary>Headers and the row's cells, tab-separated (pastes as two spreadsheet rows).</summary>
    private string RowText(int row)
    {
        var columns = Enumerable.Range(0, _table.ColumnCount).ToArray();
        return string.Join('	', columns.Select(_table.GetHeader)) + Environment.NewLine
               + string.Join('	', columns.Select(c => _table.GetCell(row, c)));
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
