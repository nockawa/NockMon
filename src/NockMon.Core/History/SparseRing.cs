namespace NockMon.Core.History;

/// <summary>
/// A ring of floats allocated by pages of <see cref="PageSize"/> slots, on the first non-zero write: most per-name
/// process series are zero most of the time (idle disk, network, GPU) or short-lived (a process that ran for a minute
/// keeps an hour of history), and a dense ring per field and name costs the whole retention up front.
/// A page is kept once allocated (reused when the ring wraps).
/// </summary>
internal sealed class SparseRing(int capacity)
{
    public const int PageSize = 64;

    private readonly float[]?[] _pages = new float[]?[(capacity + PageSize - 1) / PageSize];

    public int Capacity { get; } = capacity;

    public float this[int slot] => _pages[slot / PageSize] is { } page ? page[slot % PageSize] : 0;

    public void Add(int slot, float value)
    {
        if (value == 0)
            return;
        var page = _pages[slot / PageSize] ??= new float[PageSize];
        page[slot % PageSize] += value;
    }

    /// <summary>Keeps the larger of the slot's value and <paramref name="value"/> (values are not negative).</summary>
    public void Max(int slot, float value)
    {
        if (value <= 0)
            return;
        ref var v = ref (_pages[slot / PageSize] ??= new float[PageSize])[slot % PageSize];
        if (value > v)
            v = value;
    }

    public void Clear(int slot)
    {
        if (_pages[slot / PageSize] is { } page)
            page[slot % PageSize] = 0;
    }

    /// <summary>Allocated pages (tests).</summary>
    internal int PageCount => _pages.Count(p => p is not null);

    /// <summary>The latest <c>destination.Length</c> values before <paramref name="total"/> (exclusive), oldest first.</summary>
    public void CopyLatest(long total, Span<float> destination)
    {
        var n = destination.Length;
        var start = (int)((total - n) % Capacity);
        var written = 0;
        while (written < n)
        {
            var slot = (start + written) % Capacity;
            var offset = slot % PageSize;
            var run = Math.Min(Math.Min(PageSize - offset, Capacity - slot), n - written);
            if (_pages[slot / PageSize] is { } page)
                page.AsSpan(offset, run).CopyTo(destination.Slice(written, run));
            else
                destination.Slice(written, run).Clear();
            written += run;
        }
    }
}
