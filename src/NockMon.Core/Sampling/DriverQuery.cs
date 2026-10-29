namespace NockMon.Core.Sampling;

/// <summary>Sort and top-N over the scan's drivers. Owns its buffer: results stay valid after the engine lock.</summary>
public sealed class DriverQuery
{
    private readonly Comparison<DriverStats> _comparison;
    private readonly List<DriverStats> _rows = [];
    private DriverField _field;
    private bool _descending;
    private int _count;

    public DriverQuery() => _comparison = Compare;

    public ReadOnlySpan<DriverStats> Results => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_rows)[.._count];

    public ReadOnlySpan<DriverStats> Run(ProcessTable table, DriverField sortBy, bool descending, int top)
    {
        _rows.Clear();
        _rows.AddRange(table.Drivers);
        _field = sortBy;
        _descending = descending;
        _rows.Sort(_comparison);
        _count = Math.Min(Math.Max(top, 0), _rows.Count);
        return Results;
    }

    private int Compare(DriverStats a, DriverStats b)
    {
        var c = _field == DriverField.Name
            ? string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            : a.GetValue(_field).CompareTo(b.GetValue(_field));
        if (_descending)
            c = -c;
        return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }
}
