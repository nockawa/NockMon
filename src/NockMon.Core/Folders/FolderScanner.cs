using System.Diagnostics;

namespace NockMon.Core.Folders;

/// <summary>A scope to keep scanned: at least every <see cref="Interval"/>, and soon after a change when <see cref="WatchChanges"/>.</summary>
public readonly record struct FolderRequest(FolderScope Scope, TimeSpan Interval, bool WatchChanges);

/// <summary>
/// Scans the folders the layout uses, on one background thread, one folder at a time (a big tree must not starve the
/// disk for the others, nor several walks compete for it).
/// <para>
/// Pacing: a folder is rescanned at its interval, never sooner than 9× its last scan's duration after it ended (a walk
/// takes at most 10 % of a core over time, whatever the tree). With <see cref="FolderRequest.WatchChanges"/>, a
/// FileSystemWatcher brings the next scan forward: 2 s after the first change (a burst of writes is one rescan),
/// at most every <see cref="MinChangeInterval"/>. The interval stays as a safety net: watchers miss changes when their
/// buffer overflows and on some network shares.
/// </para>
/// </summary>
public sealed class FolderScanner : IDisposable
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MinChangeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ChangeDelay = TimeSpan.FromSeconds(2);
    private const int DutyFactor = 9;

    private readonly Lock _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Dictionary<FolderScope, Watched> _watched = [];
    private readonly Thread _thread;
    private volatile bool _disposed;
    private volatile IReadOnlyList<FolderReading> _readings = [];

    private sealed class Watched(FolderRequest request)
    {
        public FolderRequest Request = request;
        public FileSystemWatcher? Watcher;
        public long Due = Stopwatch.GetTimestamp();
        public long LastStart;
        public long LastEnd;
        public long FirstChange; // 0 = no change since the last scan.
        public FolderScanResult? Last;
        public FolderScanResult? LastGood;
        public CancellationTokenSource? Scanning;
    }

    public FolderScanner()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "NockMon folder scanner", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Raised on the scanner thread after each scan.</summary>
    public event Action<FolderScope>? Scanned;

    /// <summary>The latest valid counts of each watched scope that has one (scopes failing since they were added have none).</summary>
    public IReadOnlyList<FolderReading> Readings => _readings;

    /// <summary>
    /// Folders to keep scanned. Requests for the same scope merge: the shortest interval, watching if any asks. Scopes no
    /// longer requested stop (a scan in progress is cancelled); kept ones keep their results and schedule.
    /// </summary>
    public void SetWatched(IEnumerable<FolderRequest> requests)
    {
        var merged = new Dictionary<FolderScope, FolderRequest>();
        foreach (var r in requests)
        {
            if (string.IsNullOrWhiteSpace(r.Scope.Path))
                continue;
            var interval = r.Interval < MinInterval ? MinInterval : r.Interval;
            merged[r.Scope] = merged.TryGetValue(r.Scope, out var m)
                ? m with { Interval = interval < m.Interval ? interval : m.Interval, WatchChanges = m.WatchChanges || r.WatchChanges }
                : r with { Interval = interval };
        }

        lock (_gate)
        {
            foreach (var (scope, watched) in _watched.ToArray())
            {
                if (merged.ContainsKey(scope))
                    continue;
                watched.Scanning?.Cancel();
                watched.Watcher?.Dispose();
                _watched.Remove(scope);
            }
            foreach (var (scope, request) in merged)
            {
                if (_watched.TryGetValue(scope, out var watched))
                {
                    var sooner = request.Interval < watched.Request.Interval;
                    watched.Request = request;
                    if (sooner && watched.LastStart != 0)
                        watched.Due = Math.Min(watched.Due, watched.LastStart + Ticks(request.Interval));
                    UpdateWatcher(watched);
                }
                else
                {
                    _watched[scope] = watched = new Watched(request);
                    UpdateWatcher(watched);
                }
            }
            PublishReadings();
        }
        _wake.Set();
    }

    /// <summary>The last scan of <paramref name="scope"/> (error included), or null before the first one.</summary>
    public FolderScanResult? Latest(FolderScope scope)
    {
        lock (_gate)
            return _watched.TryGetValue(scope, out var w) ? w.Last : null;
    }

    /// <summary>When <paramref name="scope"/> is next scanned (UTC), or null if not watched.</summary>
    public DateTime? NextScan(FolderScope scope)
    {
        lock (_gate)
        {
            if (!_watched.TryGetValue(scope, out var w))
                return null;
            var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), Math.Max(w.Due, Stopwatch.GetTimestamp()));
            return DateTime.UtcNow + wait;
        }
    }

    /// <summary>Scopes currently watched.</summary>
    public IReadOnlyList<FolderScope> Scopes
    {
        get
        {
            lock (_gate)
                return _watched.Keys.ToArray();
        }
    }

    private void Run()
    {
        while (!_disposed)
        {
            Watched? next = null;
            int waitMs;
            lock (_gate)
            {
                foreach (var w in _watched.Values)
                {
                    if (next is null || w.Due < next.Due)
                        next = w;
                }
                var now = Stopwatch.GetTimestamp();
                waitMs = next is null ? Timeout.Infinite : (int)Math.Clamp((next.Due - now) * 1000 / Stopwatch.Frequency, 0, int.MaxValue);
                if (waitMs == 0)
                {
                    next!.Scanning = new CancellationTokenSource();
                    next.LastStart = now;
                    next.FirstChange = 0;
                }
            }
            if (waitMs != 0)
            {
                _wake.WaitOne(waitMs);
                continue;
            }

            var item = next!;
            FolderScanResult? result;
            try
            {
                result = FolderScan.Run(item.Request.Scope, item.Scanning!.Token);
            }
            catch (OperationCanceledException)
            {
                result = null; // Removed while scanning.
            }

            lock (_gate)
            {
                item.Scanning!.Dispose();
                item.Scanning = null;
                if (result is null || !_watched.TryGetValue(item.Request.Scope, out var current) || current != item)
                    continue;
                item.Last = result;
                if (result.Error is null)
                    item.LastGood = result;
                item.LastEnd = Stopwatch.GetTimestamp();
                var floor = item.LastEnd + DutyFactor * (item.LastEnd - item.LastStart);
                item.Due = Math.Max(item.LastStart + Ticks(item.Request.Interval), floor);
                if (item.FirstChange != 0)
                    ScheduleChange(item); // Changed during the scan.
                UpdateWatcher(item); // The folder may have appeared (or vanished).
                PublishReadings();
            }
            Scanned?.Invoke(item.Request.Scope);
        }
    }

    private void OnChanged(Watched watched)
    {
        lock (_gate)
        {
            if (watched.FirstChange != 0)
                return;
            watched.FirstChange = Stopwatch.GetTimestamp();
            ScheduleChange(watched);
        }
        _wake.Set();
    }

    /// <summary>Brings the next scan forward after a change: <see cref="ChangeDelay"/> after it, within the pacing limits.</summary>
    private static void ScheduleChange(Watched w)
    {
        var due = w.FirstChange + Ticks(ChangeDelay);
        if (w.LastEnd != 0)
            due = Math.Max(due, Math.Max(w.LastStart + Ticks(MinChangeInterval), w.LastEnd + DutyFactor * (w.LastEnd - w.LastStart)));
        w.Due = Math.Min(w.Due, due);
    }

    /// <summary>A watcher while changes are watched and the folder exists; recreated when its settings changed.</summary>
    private void UpdateWatcher(Watched w)
    {
        var scope = w.Request.Scope;
        if (!w.Request.WatchChanges || !Directory.Exists(scope.FullPath))
        {
            w.Watcher?.Dispose();
            w.Watcher = null;
            return;
        }
        if (w.Watcher is not null)
            return;
        try
        {
            var watcher = new FileSystemWatcher(scope.FullPath)
            {
                IncludeSubdirectories = scope.Recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += (_, _) => OnChanged(w);
            watcher.Created += (_, _) => OnChanged(w);
            watcher.Deleted += (_, _) => OnChanged(w);
            watcher.Renamed += (_, _) => OnChanged(w);
            watcher.Error += (_, _) => OnChanged(w); // Overflow: something changed, rescan.
            watcher.EnableRaisingEvents = true;
            w.Watcher = watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            w.Watcher = null; // The interval still applies.
        }
    }

    private void PublishReadings() =>
        _readings = _watched.Values.Where(w => w.LastGood is not null).Select(w => new FolderReading(w.Request.Scope, w.LastGood!)).ToArray();

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var w in _watched.Values)
            {
                w.Scanning?.Cancel();
                w.Watcher?.Dispose();
            }
            _watched.Clear();
        }
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
        _wake.Dispose();
    }
}
