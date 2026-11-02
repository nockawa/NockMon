using Microsoft.UI.Dispatching;
using NockMon.Core.Metrics;
using NockMon.Core.Sampling;

namespace NockMon.Monitoring;

/// <summary>
/// UI-side access to the sampling engine. Sampler notifications are marshalled to the UI thread and
/// coalesced: if the UI is still behind, intermediate ticks are dropped instead of queuing up.
/// </summary>
public sealed class MonitorService
{
    private readonly DispatcherQueue _dispatcher;
    private int _pending;
    private CpuScale _cpuScale;

    public MonitorService(SamplingEngine engine, DispatcherQueue dispatcher, CpuScale cpuScale)
    {
        Engine = engine;
        _dispatcher = dispatcher;
        _cpuScale = cpuScale;
        engine.Sampled += OnSampled;
    }

    public SamplingEngine Engine { get; }

    public int LogicalProcessors => Engine.LogicalProcessorCount;

    public CpuScale CpuScale
    {
        get => _cpuScale;
        set
        {
            if (_cpuScale == value)
                return;
            _cpuScale = value;
            DisplayVersion++;
            Updated?.Invoke();
        }
    }

    /// <summary>Incremented when display settings change (widgets caching formatted text must re-format).</summary>
    public int DisplayVersion { get; private set; }

    /// <summary>Raised on the UI thread after a new sample, or when display settings change.</summary>
    public event Action? Updated;

    public string Format(double value, MetricUnit unit) => MetricFormatter.Format(value, unit, _cpuScale, LogicalProcessors);

    /// <summary>Forces a UI update (e.g. after a widget was reconfigured).</summary>
    public void RaiseUpdated() => Updated?.Invoke();

    private void OnSampled()
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0)
            return;
        _deliver ??= () =>
        {
            Volatile.Write(ref _pending, 0);
            Updated?.Invoke();
        };
        if (!_dispatcher.TryEnqueue(_deliver))
            Volatile.Write(ref _pending, 0);
    }

    /// <summary>One handler for every tick: a lambda per sample would allocate it and its WinRT wrapper each time.</summary>
    private DispatcherQueueHandler? _deliver;
}
