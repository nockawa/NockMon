using NockMon.Sdk;
using ServiceStatus.NockMon.Status;

namespace ServiceStatus.NockMon;

/// <summary>
/// The watched services in the background, for the whole session: their states as NockMon metrics ("History" ticked in a
/// widget's settings), so NockMon keeps their history, charts and value widgets can show them, AI agents can read them.
/// Value: 0 operational, 1 maintenance, 2 degraded, 3 partial outage, 4 major outage; unknown is missing.
/// </summary>
public sealed class StatusMetricPlugin : IMetricPlugin
{
    public string Id => "status";

    public string DisplayName => "Service status";

    public void Start(IPluginContext context) => Background.Start(context.DataDirectory);

    public IReadOnlyList<PluginMetric> Metrics => Background.Metrics;

    public bool TryRead(IReadOnlyList<PluginMetric> metrics, Span<double> values) => Background.TryRead(metrics, values);

    public void Dispose() => Background.Stop();
}

/// <summary>
/// Behind <see cref="StatusMetricPlugin"/>: subscribes the poller to the watched services, and every 10 seconds turns their
/// snapshots into metric values (read on NockMon's sampling thread: a lock and a copy).
/// </summary>
internal static class Background
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);
    private static readonly Lock Gate = new();
    private static string? _dataDirectory;
    private static IDisposable? _subscription;
    private static CancellationTokenSource? _loop;
    private static (CatalogEntry Entry, StatusFilter Filter)[] _recorded = [];
    private static IReadOnlyList<PluginMetric> _metrics = [];
    private static double[] _values = [];

    public static IReadOnlyList<PluginMetric> Metrics => _metrics;

    public static void Start(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        Apply(Watchlist.Load(dataDirectory));
        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        _ = Task.Run(() => LoopAsync(token));
    }

    /// <summary>From a widget's settings: saves the watchlist and, the background running, applies it at once.</summary>
    public static void Configure(string dataDirectory, IReadOnlyList<Watched> watched)
    {
        Watchlist.Save(dataDirectory, watched);
        if (_dataDirectory is not null)
            Apply(watched);
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _loop?.Cancel();
            _loop = null;
            _subscription?.Dispose();
            _subscription = null;
            _dataDirectory = null;
        }
    }

    public static bool TryRead(IReadOnlyList<PluginMetric> metrics, Span<double> values)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(metrics, _metrics))
                return false;
            _values.AsSpan(0, Math.Min(values.Length, _values.Length)).CopyTo(values);
            return true;
        }
    }

    private static async Task LoopAsync(CancellationToken cancel)
    {
        using var timer = new PeriodicTimer(Tick);
        do
        {
            Evaluate();
        }
        while (await Next(timer, cancel));

        static async Task<bool> Next(PeriodicTimer timer, CancellationToken cancel)
        {
            try
            {
                return await timer.WaitForNextTickAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private static void Evaluate()
    {
        var now = DateTimeOffset.UtcNow;
        lock (Gate)
        {
            for (var i = 0; i < _recorded.Length; i++)
                _values[i] = Watchlist.Value(State(_recorded[i].Entry, _recorded[i].Filter, now).Severity);
        }
    }

    private static ServiceState State(CatalogEntry entry, StatusFilter filter, DateTimeOffset now)
    {
        var snapshot = StatusPoller.Get(entry.Id);
        return Evaluation.Evaluate(entry, filter, snapshot?.Reading, snapshot?.ReadAt, snapshot?.Error, now,
            snapshot?.StaleAfter ?? StatusPoller.StaleAfter(entry.Kind));
    }

    private static void Apply(IReadOnlyList<Watched> watched)
    {
        var directory = _dataDirectory!;
        var catalog = Catalog.Load(directory).ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var resolved = watched.Where(w => catalog.ContainsKey(w.Id)).Select(w => (catalog[w.Id], w)).ToArray();
        var subscription = resolved.Length > 0 ? StatusPoller.Subscribe(directory, resolved.Select(r => r.Item1).ToArray()) : null;
        IDisposable? previous;
        lock (Gate)
        {
            previous = _subscription;
            _subscription = subscription;
            var recorded = resolved.Where(r => r.w.History).Select(r => (r.Item1, r.w.Filter)).ToArray();
            // A new list only when the recorded services change: NockMon compares references, and restarts their history.
            if (!recorded.Select(r => r.Item1.Id).SequenceEqual(_recorded.Select(r => r.Entry.Id)))
            {
                _values = new double[recorded.Length];
                _values.AsSpan().Fill(double.NaN);
                _metrics = recorded.Select(r => new PluginMetric(Watchlist.MetricId(r.Item1.Id), $"{r.Item1.Name} status", r.Item1.Name, PluginMetricUnit.Count, "Service status"))
                    .ToArray();
            }
            _recorded = recorded;
        }
        previous?.Dispose(); // After the new subscription: services kept are never left unpolled.
        Evaluate();
    }
}
