using System.Diagnostics;
using MyCpuMonitor.Core.History;

namespace MyCpuMonitor.Core.Sampling;

/// <summary>Sampling rates. The scan interval is rounded up to a whole number of sample intervals.</summary>
public readonly record struct SamplingOptions(TimeSpan Interval, TimeSpan ScanInterval, TimeSpan Retention)
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Full scans happen every <see cref="ScanEvery"/> samples (rounded up: never scan more often than asked).</summary>
    public int ScanEvery => Math.Max(1, (int)Math.Ceiling(ScanInterval / Interval - 1e-9));

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Interval, MinInterval, nameof(Interval));
        ArgumentOutOfRangeException.ThrowIfLessThan(ScanInterval, Interval, nameof(ScanInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(Retention, ScanInterval * 2, nameof(Retention));
    }
}

/// <summary>
/// Single sampler for the whole app, on a dedicated thread with drift-free deadlines. Each sample (fast rate) reads
/// system counters and the watched processes (cheap); every <see cref="SamplingOptions.ScanEvery"/> samples a full
/// process scan (expensive) refreshes the process table, discovers watched processes and feeds per-name history.
/// Rates are computed outside the lock; the snapshot swap and history append happen under it.
/// Consumers access data only through <see cref="Read{TState,TResult}"/>.
/// </summary>
public sealed class SamplingEngine : IDisposable
{
    private readonly ISystemProbe _probe;
    private readonly SampleProcessor _processor = new();
    private readonly WatchedProcessTracker _watched;
    private readonly RawSample _raw = new();
    private readonly Lock _lock = new();
    private readonly Lock _sampleLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private Snapshot _front = new();
    private Snapshot _back = new();
    private ProcessTable _currentTable;
    private ProcessTable _spareTable = new();
    private bool _tableHasRates;
    private int _samplesUntilScan;
    private HistoryStore _history;
    private SamplingOptions _options;
    private string[]? _pendingPatterns;
    private Thread? _thread;
    private volatile bool _stopping;
    private readonly Etw.IKernelEventSource? _events;
    private readonly Gpu.IGpuSource? _gpu;
    private readonly Dictionary<int, Etw.ProcessIoTotals> _scanIo = new();
    private readonly List<Etw.DriverTotals> _scanDrivers = [];
    private readonly Dictionary<int, double> _gpuByPid = new();

    /// <param name="events">Optional kernel event stream (ETW) for disk, network and DPC/ISR metrics. Not owned.</param>
    /// <param name="gpu">Optional GPU source. Not owned.</param>
    public SamplingEngine(ISystemProbe probe, SamplingOptions options, Etw.IKernelEventSource? events = null, Gpu.IGpuSource? gpu = null)
    {
        options.Validate();
        _probe = probe;
        _events = events;
        _gpu = gpu;
        _watched = new WatchedProcessTracker(probe);
        _options = options;
        _history = CreateHistory();
        _currentTable = _front.Table;
        _back.Table = _currentTable;
    }

    /// <summary>Raised on the sampler thread after each sample is published.</summary>
    public event Action? Sampled;

    /// <summary>Raised on the sampler thread when a sample fails; sampling continues.</summary>
    public event Action<Exception>? SampleFailed;

    public int LogicalProcessorCount => _probe.LogicalProcessorCount;

    /// <summary>Kernel event stream, if any (status, process event log).</summary>
    public Etw.IKernelEventSource? KernelEvents => _events;

    /// <summary>GPU adapters (empty without a GPU source).</summary>
    public IReadOnlyList<Gpu.GpuAdapterInfo> GpuAdapters => _gpu?.Adapters ?? [];

    /// <summary>GPU engines to poll (each costs ~0.3 ms per sample): (adapter name, engine name pattern).</summary>
    public void SetWatchedGpuEngines(IEnumerable<(string Adapter, string Pattern)> engines) => _gpu?.SetWatchedEngines(engines);

    public SamplingOptions Options
    {
        get { lock (_lock) return _options; }
    }

    public TimeSpan Interval => Options.Interval;

    public bool IsRunning => _thread is not null;

    /// <summary>Wall time spent producing the last published sample (system + watched + history, plus the scan if any).</summary>
    public TimeSpan LastSampleDuration { get; private set; }

    /// <summary>Wall time of the last full process scan (probe + rates).</summary>
    public TimeSpan LastScanDuration { get; private set; }

    /// <summary>Processes currently sampled at the fast rate.</summary>
    public int WatchedProcessCount { get; private set; }

    /// <summary>Changes rates/retention. History is cleared (its slots are tied to the intervals).</summary>
    public void Configure(SamplingOptions options)
    {
        options.Validate();
        lock (_lock)
        {
            if (options == _options)
                return;
            _options = options;
            _history = CreateHistory();
            _samplesUntilScan = 0;
        }
        _wake.Set();
    }

    /// <summary>Name patterns to sample at the fast rate (applied at the next sample, effective from the next scan).</summary>
    public void SetWatchedPatterns(IEnumerable<string> patterns)
    {
        var array = patterns.ToArray();
        lock (_lock)
            _pendingPatterns = array;
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

    /// <summary>Takes one sample synchronously. The first sample after start only establishes the baselines.</summary>
    /// <returns>true if a sample was published.</returns>
    public bool SampleOnce()
    {
        lock (_sampleLock)
        {
            var start = Stopwatch.GetTimestamp();
            string[]? patterns;
            lock (_lock)
            {
                patterns = _pendingPatterns;
                _pendingPatterns = null;
            }
            if (patterns is not null)
            {
                _watched.SetPatterns(patterns);
                _samplesUntilScan = 0; // Discover the new patterns' processes now.
            }

            _probe.CollectSystem(_raw);

            // Scan on schedule, and on every sample until the table has rates (startup, reconfiguration).
            var scanned = false;
            if (_samplesUntilScan <= 0 || !_tableHasRates)
            {
                var scanStart = Stopwatch.GetTimestamp();
                _probe.CollectProcesses(_raw);
                scanned = _processor.ProcessScan(_raw, _spareTable);
                if (_events is not null)
                {
                    _events.HarvestScan(_scanIo, _scanDrivers);
                    if (scanned)
                        SampleProcessor.ApplyScanEvents(_spareTable, _scanIo, _scanDrivers);
                }
                if (_gpu is not null)
                {
                    _gpu.CollectProcesses(_gpuByPid);
                    if (scanned)
                    {
                        foreach (ref var p in _spareTable.Items.AsSpan(0, _spareTable.Count))
                            p.GpuPercent = _gpuByPid.GetValueOrDefault(p.Pid);
                    }
                }
                _watched.OnScan(_raw, scanned ? _spareTable : null);
                _samplesUntilScan = Options.ScanEvery;
                LastScanDuration = Stopwatch.GetElapsedTime(scanStart);
            }
            _samplesUntilScan--;

            _watched.Tick();
            WatchedProcessCount = _watched.WatchedCount;

            Etw.KernelEventTotals events = default;
            _events?.HarvestSample(out events);
            if (!_processor.ProcessSystem(_raw, _back))
                return false;
            SampleProcessor.ApplyEvents(_back, in events);
            _gpu?.Collect(_back.Gpus);

            lock (_lock)
            {
                if (scanned)
                {
                    // The previous table stays referenced by the front snapshot until the swap below; the one before it
                    // is unreferenced and becomes the spare for the next scan.
                    (_currentTable, _spareTable) = (_spareTable, _currentTable);
                    _tableHasRates = true;
                }
                _back.Table = _currentTable;
                _back.System.IoOps = _currentTable.IoOps;
                _back.System.IoBytes = _currentTable.IoBytes;
                _back.System.ProcessorQueueLength = _currentTable.ReadyThreads;

                if (_history.CoreCount != _back.Cores.Length)
                    _history = new HistoryStore(_history.Capacity, _history.ScanCapacity, _back.Cores.Length);
                (_front, _back) = (_back, _front);
                _front.SampleIndex = _history.TotalSamples;
                _history.AppendSample(_front, _watched.Aggregates);
                if (scanned)
                {
                    _currentTable.ScanIndex = _history.TotalScans;
                    _history.AppendScan(_currentTable);
                }
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

    private HistoryStore CreateHistory() => new(
        Math.Max(2, (int)Math.Ceiling(_options.Retention / _options.Interval)),
        Math.Max(2, (int)Math.Ceiling(_options.Retention / (_options.Interval * _options.ScanEvery))),
        _probe.LogicalProcessorCount);

    public void Dispose()
    {
        Stop();
        _watched.Dispose();
        _wake.Dispose();
    }
}
