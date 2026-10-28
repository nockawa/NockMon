using System.Diagnostics;
using MyCpuMonitor.Core.History;

namespace MyCpuMonitor.Core.Sampling;

/// <summary>
/// Single sampler for the whole app: a dedicated thread reads the probe at a fixed interval
/// (drift-free deadlines), computes rates outside the lock, then swaps the double-buffered snapshot
/// and appends to history under the lock. Consumers access data only through <see cref="Read{TState,TResult}"/>.
/// </summary>
public sealed class SamplingEngine : IDisposable
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);

    private readonly ISystemProbe _probe;
    private readonly SampleProcessor _processor = new();
    private readonly RawSample _raw = new();
    private readonly Lock _lock = new();
    private readonly Lock _sampleLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private Snapshot _front = new();
    private Snapshot _back = new();
    private HistoryStore _history;
    private TimeSpan _interval;
    private TimeSpan _retention;
    private Thread? _thread;
    private volatile bool _stopping;

    public SamplingEngine(ISystemProbe probe, TimeSpan interval, TimeSpan retention)
    {
        _probe = probe;
        Validate(interval, retention);
        _interval = interval;
        _retention = retention;
        _history = CreateHistory();
    }

    /// <summary>Raised on the sampler thread after each sample is published.</summary>
    public event Action? Sampled;

    /// <summary>Raised on the sampler thread when a sample fails; sampling continues.</summary>
    public event Action<Exception>? SampleFailed;

    public int LogicalProcessorCount => _probe.LogicalProcessorCount;

    public TimeSpan Interval
    {
        get { lock (_lock) return _interval; }
    }

    public TimeSpan Retention
    {
        get { lock (_lock) return _retention; }
    }

    public bool IsRunning => _thread is not null;

    /// <summary>Wall time spent producing the last published sample (probe + processing + history).</summary>
    public TimeSpan LastSampleDuration { get; private set; }

    /// <summary>Changes interval/retention. History is cleared (its slots are tied to the interval).</summary>
    public void Configure(TimeSpan interval, TimeSpan retention)
    {
        Validate(interval, retention);
        lock (_lock)
        {
            if (interval == _interval && retention == _retention)
                return;
            _interval = interval;
            _retention = retention;
            _history = CreateHistory();
        }
        _wake.Set();
    }

    public void Start()
    {
        if (_thread is not null)
            return;
        _stopping = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "MyCpuMonitor sampler", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread is null)
            return;
        _stopping = true;
        _wake.Set();
        _thread.Join();
        _thread = null;
    }

    /// <summary>Takes one sample synchronously. The first sample after start only establishes the baseline.</summary>
    /// <returns>true if a sample was published.</returns>
    public bool SampleOnce()
    {
        lock (_sampleLock)
        {
            var start = Stopwatch.GetTimestamp();
            _probe.Collect(_raw);
            if (!_processor.Process(_raw, _back))
                return false;

            lock (_lock)
            {
                if (_history.CoreCount != _back.Cores.Length)
                    _history = new HistoryStore(_history.Capacity, _back.Cores.Length);
                (_front, _back) = (_back, _front);
                _front.SampleIndex = _history.TotalSamples;
                _history.Append(_front);
            }
            LastSampleDuration = Stopwatch.GetElapsedTime(start);
        }
        Sampled?.Invoke();
        return true;
    }

    /// <summary>
    /// Runs <paramref name="reader"/> under the data lock with the latest snapshot and the history.
    /// Copy what you need: references into the snapshot are invalid after the callback. Keep it short.
    /// </summary>
    public TResult Read<TState, TResult>(TState state, Func<TState, Snapshot, HistoryStore, TResult> reader)
    {
        lock (_lock)
            return reader(state, _front, _history);
    }

    private void Run()
    {
        var next = Stopwatch.GetTimestamp();
        while (!_stopping)
        {
            try
            {
                SampleOnce();
            }
            catch (Exception e)
            {
                SampleFailed?.Invoke(e);
            }

            var intervalTicks = (long)(Interval.TotalSeconds * Stopwatch.Frequency);
            next += intervalTicks;
            var now = Stopwatch.GetTimestamp();
            if (next < now)
                next = now; // Fell behind (suspend, debugger): resynchronize instead of bursting.

            var waitMs = (int)Math.Ceiling((next - now) * 1000.0 / Stopwatch.Frequency);
            if (_wake.WaitOne(waitMs))
                next = Stopwatch.GetTimestamp(); // Reconfigured or stopping: restart the schedule now.
        }
    }

    private HistoryStore CreateHistory()
    {
        var capacity = (int)Math.Ceiling(_retention / _interval);
        return new HistoryStore(Math.Max(capacity, 2), _probe.LogicalProcessorCount);
    }

    private static void Validate(TimeSpan interval, TimeSpan retention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, MinInterval);
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, interval * 2);
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }
}
