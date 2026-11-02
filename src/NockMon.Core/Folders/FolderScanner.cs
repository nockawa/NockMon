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
/// <para>
/// Disk access (scans, watcher creation) happens on the scanner thread, never under the lock the UI takes: an
/// unreachable share stalls the scans, not the window.
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
        public bool WatcherDirty = true; // The watcher must be (re)considered: set under the lock, done by the scanner thread.
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

        var stopped = new List<FileSystemWatcher>();
        lock (_gate)
        {
            foreach (var (scope, watched) in _watched.ToArray())
            {
                if (merged.ContainsKey(scope))
                    continue;
                watched.Scanning?.Cancel();
                if (watched.Watcher is { } watcher)
                    stopped.Add(watcher);
                watched.Watcher = null;
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
                    watched.WatcherDirty = true;
                }
                else
                {
                    _watched[scope] = new Watched(request);
                }
            }
            PublishReadings();
        }
        foreach (var watcher in stopped)
            watcher.Dispose();
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
            UpdateWatchers();
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
            catch (OperationCanceledException) when (item.Scanning!.IsCancellationRequested)
            {
                result = null; // Removed while scanning.
            }
            catch (OperationCanceledException e)
            {
                // Not ours: an I/O aborted by the system (ERROR_OPERATION_ABORTED on a network share). A failed scan,
                // paced like any other, not a removal to retry at once.
                result = new FolderScanResult(DateTime.UtcNow, 0, 0, 0, 0, TimeSpan.Zero, [], e.Message);
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
                item.WatcherDirty = true; // The folder may have appeared (or vanished).
                PublishReadings();
            }
            Scanned?.Invoke(item.Request.Scope);
        }
    }

    private void OnChanged(Watched watched)
    {
        if (_disposed)
            return; // A callback already in flight when the scanner was disposed.
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

    /// <summary>
    /// Scanner thread: a watcher for each scope whose changes are watched and whose folder exists, none for the others.
    /// The folder checks and watcher creation run outside the lock.
    /// </summary>
    private void UpdateWatchers()
    {
        List<(Watched Item, bool Has)> dirty;
        lock (_gate)
        {
            dirty = _watched.Values.Where(w => w.WatcherDirty).Select(w => (w, w.Watcher is not null)).ToList();
            foreach (var (item, _) in dirty)
                item.WatcherDirty = false;
        }
        foreach (var (item, has) in dirty)
        {
            var scope = item.Request.Scope;
            var wanted = item.Request.WatchChanges && Directory.Exists(scope.FullPath);
            var created = wanted && !has ? CreateWatcher(item) : null;
            FileSystemWatcher? obsolete = created;
            lock (_gate)
            {
                var current = _watched.TryGetValue(scope, out var w) && w == item;
                if (current && !wanted)
                {
                    obsolete = item.Watcher;
                    item.Watcher = null;
                }
                else if (current && created is not null && item.Watcher is null)
                {
                    item.Watcher = created;
                    obsolete = null;
                }
            }
            obsolete?.Dispose();
        }
    }

    private FileSystemWatcher? CreateWatcher(Watched w)
    {
        var scope = w.Request.Scope;
        try
        {
            // LastWrite and not also Size: one event per write instead of two. Changes inside excluded folders (.git,
            // node_modules churn) cannot change the counts: no rescan for them.
            var root = scope.FullPath;
            var exclude = FolderScan.Patterns(scope.Exclude);
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = scope.Recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            void Changed(FileSystemEventArgs e)
            {
                if (!FolderScan.IsExcluded(root, exclude, e.FullPath))
                    OnChanged(w);
            }
            watcher.Changed += (_, e) => Changed(e);
            watcher.Created += (_, e) => Changed(e);
            watcher.Deleted += (_, e) => Changed(e);
            watcher.Renamed += (_, e) => Changed(e);
            watcher.Error += (_, _) => OnChanged(w); // Overflow: something changed, rescan.
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null; // The interval still applies.
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
        // _wake is not disposed: a watcher callback already in flight may still set it.
    }
}
