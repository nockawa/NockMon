using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Sampling;
using MyCpuMonitor.Monitoring;

namespace MyCpuMonitor.Widgets;

/// <summary>
/// Top-N kernel pool tags (scan rate). Header click sorts. Sustained positive growth of a tag is the signature of a
/// driver leak.
/// </summary>
public sealed partial class PoolTagTableWidget : UserControl, IMonitorWidget
{
    private static readonly (PoolTagField Field, CellColumn Column)[] Columns =
    [
        (PoolTagField.Tag, new("Tag", "Pool tag (4 characters, owner driver/component)", 52, RightAligned: false, Monospace: true)),
        (PoolTagField.NonPagedBytes, new("Non-paged", "Non-paged pool bytes", 80)),
        (PoolTagField.PagedBytes, new("Paged", "Paged pool bytes", 80)),
        (PoolTagField.NonPagedGrowth, new("NP growth", "Non-paged bytes per second since the previous scan", 84)),
        (PoolTagField.PagedGrowth, new("P growth", "Paged bytes per second since the previous scan", 84)),
        (PoolTagField.AllocationRate, new("Allocs", "Allocations per second", 72)),
        (PoolTagField.Outstanding, new("Live", "Allocations not freed", 72)),
    ];

    private readonly MonitorService _monitor;
    private readonly PoolTagTableWidgetConfig _config;
    private readonly PoolTagQuery _query = new();
    private readonly CellTable _table;
    private long _lastScanIndex = -1;
    private bool _dirty = true;

    public PoolTagTableWidget(PoolTagTableWidgetConfig config, MonitorService monitor)
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

        var rows = _query.Results;
        for (var r = 0; r < rows.Length && r < _table.RowCount; r++)
        {
            for (var c = 0; c < Columns.Length; c++)
                _table.SetCell(r, c, Format(in rows[r], Columns[c].Field));
        }
        _table.ClearFrom(rows.Length);
    }

    private static string Format(in PoolTagStats tag, PoolTagField field) => field switch
    {
        PoolTagField.Tag => tag.Tag,
        PoolTagField.NonPagedBytes or PoolTagField.PagedBytes or PoolTagField.TotalBytes => MetricFormatter.FormatBytes(tag.GetValue(field)),
        PoolTagField.NonPagedGrowth or PoolTagField.PagedGrowth => FormatGrowth(tag.GetValue(field)),
        PoolTagField.AllocationRate => tag.AllocationRate.ToString("#,0", CultureInfo.CurrentCulture) + "/s",
        PoolTagField.Outstanding => tag.Outstanding.ToString("#,0", CultureInfo.CurrentCulture),
        _ => "",
    };

    private static string FormatGrowth(double bytesPerSecond) => bytesPerSecond == 0
        ? "0"
        : (bytesPerSecond > 0 ? "+" : "") + MetricFormatter.FormatBytes(bytesPerSecond) + "/s";

    private void SortBy(PoolTagField field)
    {
        if (_config.SortBy == field)
            _config.Descending = !_config.Descending;
        else
        {
            _config.SortBy = field;
            _config.Descending = field != PoolTagField.Tag;
        }
        UpdateSortIndicator();
        _dirty = true;
        Refresh();
        ConfigChanged?.Invoke();
    }

    private void UpdateSortIndicator() =>
        _table.SetSortIndicator(Array.FindIndex(Columns, c => c.Field == _config.SortBy), _config.Descending);
}
