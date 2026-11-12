using NockMon.Core.History;
using NockMon.Core.Metrics;
using NockMon.Monitoring;
using Windows.UI;

namespace NockMon.Widgets;

/// <summary>A series' latest samples, owned by a widget (buffers reused across refreshes).</summary>
public sealed class SeriesData(SeriesRef source, string label, Color color)
{
    public SeriesRef Source { get; } = source;
    public string Label { get; } = label;
    public Color Color { get; } = color;
    public float[] Values { get; private set; } = [];

    /// <summary>Valid samples in <see cref="Values"/>, oldest first.</summary>
    public int Count { get; private set; }

    /// <summary>Set when the series cannot be read (e.g. core index beyond this machine).</summary>
    public string? Error { get; private set; }

    public float Latest => Count > 0 ? Values[Count - 1] : float.NaN;

    /// <summary>Samples covering <paramref name="window"/> at the current interval, capped by the history capacity.</summary>
    public static int SamplesFor(TimeSpan window, TimeSpan interval, int capacity) =>
        Math.Clamp((int)Math.Ceiling(window / interval), 2, capacity);

    /// <summary>Reads all series in one engine lock, and the samples' times into <paramref name="times"/> if given.</summary>
    /// <returns>Window size in samples.</returns>
    public static int ReadAll(MonitorService monitor, IReadOnlyList<SeriesData> series, TimeSpan window, SampleTimes? times = null) =>
        monitor.Engine.Read((series, window, monitor.Engine.Interval, times), static (state, _, history) =>
        {
            var samples = SamplesFor(state.window, state.Item3, history.Capacity);
            foreach (var s in state.series)
                s.ReadFrom(history, samples);
            state.times?.ReadFrom(history, samples);
            return samples;
        });

    private void ReadFrom(HistoryStore history, int samples)
    {
        if (Values.Length < samples)
            Values = new float[samples];
        try
        {
            Count = history.Read(Source, Values.AsSpan(0, samples));
            Error = null;
        }
        catch (ArgumentException e)
        {
            Count = 0;
            Error = e.Message;
        }
    }
}

/// <summary>Wall-clock times (UTC) of the samples read with <see cref="SeriesData.ReadAll"/>, aligned on the newest.</summary>
public sealed class SampleTimes
{
    public DateTime[] Values { get; private set; } = [];

    public int Count { get; private set; }

    internal void ReadFrom(HistoryStore history, int samples)
    {
        if (Values.Length < samples)
            Values = new DateTime[samples];
        Count = history.ReadTimestamps(Values.AsSpan(0, samples));
    }
}
