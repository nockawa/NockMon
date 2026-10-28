namespace MyCpuMonitor.Core.Sampling;

/// <summary>Sort and top-N over the scan's pool tags. Owns its buffer: allocation-free in steady state.</summary>
public sealed class PoolTagQuery
{
    private readonly Comparison<PoolTagStats> _comparison;
    private PoolTagStats[] _rows = new PoolTagStats[4096];
    private int _count;
    private PoolTagField _field;
    private bool _descending;

    public PoolTagQuery() => _comparison = Compare;

    public ReadOnlySpan<PoolTagStats> Results => _rows.AsSpan(0, _count);

    public ReadOnlySpan<PoolTagStats> Run(ProcessTable table, PoolTagField sortBy, bool descending, int top)
    {
        if (_rows.Length < table.PoolTagCount)
            _rows = new PoolTagStats[table.PoolTagCount + table.PoolTagCount / 4];
        var rows = _rows.AsSpan(0, table.PoolTagCount);
        table.PoolTagSpan.CopyTo(rows);
        _field = sortBy;
        _descending = descending;
        rows.Sort(_comparison);
        _count = Math.Min(Math.Max(top, 0), rows.Length);
        return Results;
    }

    private int Compare(PoolTagStats a, PoolTagStats b)
    {
        var c = _field == PoolTagField.Tag
            ? string.CompareOrdinal(a.Tag, b.Tag)
            : a.GetValue(_field).CompareTo(b.GetValue(_field));
        if (_descending)
            c = -c;
        return c != 0 ? c : string.CompareOrdinal(a.Tag, b.Tag);
    }
}
