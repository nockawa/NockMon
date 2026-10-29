using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using NockMon.Core.Layout;
using NockMon.Core.Sampling;
using NockMon.Monitoring;

namespace NockMon.Widgets;

/// <summary>
/// DPC/ISR activity per driver over the last scan interval (ETW). Time columns are ms per second (1000 = one CPU);
/// the max columns are the longest single DPC/ISR, which is what causes audio dropouts and stutter (> 1 ms is bad).
/// </summary>
public sealed partial class DriverTableWidget : UserControl, IMonitorWidget
{
    private static readonly (DriverField Field, CellColumn Column)[] Columns =
    [
        (DriverField.Name, new("Driver", "Driver image owning the DPC/ISR routine", 140, RightAligned: false)),
        (DriverField.DpcTime, new("DPC ms/s", "DPC time per second (1000 = one CPU)", 72)),
        (DriverField.DpcRate, new("DPC/s", "DPCs per second", 64)),
        (DriverField.DpcMax, new("Max DPC", "Longest single DPC in the interval", 72)),
        (DriverField.IsrTime, new("ISR ms/s", "Interrupt time per second (1000 = one CPU)", 72)),
        (DriverField.IsrRate, new("ISR/s", "Interrupts per second", 64)),
        (DriverField.IsrMax, new("Max ISR", "Longest single ISR in the interval", 72)),
    ];

    private readonly MonitorService _monitor;
    private readonly DriverTableWidgetConfig _config;
    private readonly DriverQuery _query = new();
    private readonly CellTable _table;
    private long _lastScanIndex = -1;
    private bool _dirty = true;
    private string? _status;

    public DriverTableWidget(DriverTableWidgetConfig config, MonitorService monitor)
    {
        _config = config;
        _monitor = monitor;
        _table = new CellTable(Columns.Select(c => c.Column with { Doc = Core.Help.Docs.Of(c.Field) }).ToArray(), config.TopCount);
        _table.HeaderTapped += column => SortBy(Columns[column].Field);
        Content = _table;
        UpdateSortIndicator();
    }

    public event Action? ConfigChanged;

    public void Refresh()
    {
        var status = _monitor.Engine.KernelEvents?.Status ?? "Kernel events are not enabled.";
        if (_monitor.Engine.KernelEvents is { Status: null })
            status = null;
        if (status is not null)
        {
            if (status != _status)
            {
                _table.ClearFrom(0);
                _table.SetCell(0, 0, status);
                _status = status;
            }
            return;
        }
        _status = null;

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

    private static string Format(in DriverStats d, DriverField field)
    {
        var c = CultureInfo.CurrentCulture;
        return field switch
        {
            DriverField.Name => d.Name,
            DriverField.DpcTime or DriverField.IsrTime => d.GetValue(field).ToString("0.00", c),
            DriverField.DpcRate or DriverField.IsrRate => d.GetValue(field).ToString("#,0", c),
            DriverField.DpcMax or DriverField.IsrMax => FormatDuration(d.GetValue(field)),
            _ => "",
        };
    }

    /// <summary>Microseconds below 1 ms (DPC budgets are in µs).</summary>
    private static string FormatDuration(double ms) => ms <= 0
        ? ""
        : ms < 1
            ? (ms * 1000).ToString("0", CultureInfo.CurrentCulture) + " µs"
            : ms.ToString("0.00", CultureInfo.CurrentCulture) + " ms";

    private void SortBy(DriverField field)
    {
        if (_config.SortBy == field)
            _config.Descending = !_config.Descending;
        else
        {
            _config.SortBy = field;
            _config.Descending = field != DriverField.Name;
        }
        UpdateSortIndicator();
        _dirty = true;
        Refresh();
        ConfigChanged?.Invoke();
    }

    private void UpdateSortIndicator() =>
        _table.SetSortIndicator(Array.FindIndex(Columns, c => c.Field == _config.SortBy), _config.Descending);
}
