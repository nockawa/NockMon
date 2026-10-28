namespace MyCpuMonitor.Core.Sampling;

/// <summary>Sort and top-N over the scan's kernel object types. Owns its buffer: results stay valid after the engine lock.</summary>
public sealed class ObjectTypeQuery
{
    private readonly Comparison<ObjectTypeStats> _comparison;
    private ObjectTypeStats[] _rows = new ObjectTypeStats[128];
    private int _count;
    private ObjectTypeField _field;
    private bool _descending;

    public ObjectTypeQuery() => _comparison = Compare;

    public ReadOnlySpan<ObjectTypeStats> Results => _rows.AsSpan(0, _count);

    public ReadOnlySpan<ObjectTypeStats> Run(ProcessTable table, ObjectTypeField sortBy, bool descending, int top)
    {
        if (_rows.Length < table.ObjectTypeCount)
            _rows = new ObjectTypeStats[table.ObjectTypeCount];
        var rows = _rows.AsSpan(0, table.ObjectTypeCount);
        table.ObjectTypeSpan.CopyTo(rows);
        _field = sortBy;
        _descending = descending;
        rows.Sort(_comparison);
        _count = Math.Min(Math.Max(top, 0), rows.Length);
        return Results;
    }

    private int Compare(ObjectTypeStats a, ObjectTypeStats b)
    {
        var c = _field == ObjectTypeField.Name
            ? string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            : a.GetValue(_field).CompareTo(b.GetValue(_field));
        if (_descending)
            c = -c;
        return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }
}
