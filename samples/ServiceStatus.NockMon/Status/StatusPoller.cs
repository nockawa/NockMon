using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServiceStatus.NockMon.Status;

/// <summary>The latest reading of a page (kept across failures), when it was read, and the latest error.</summary>
public sealed record Snapshot(ServiceReading? Reading, DateTimeOffset? ReadAt, string? Error, TimeSpan StaleAfter);

/// <summary>
/// Shared by every widget: one poller for the whole app, polling the union of the widgets' pages while at least one
/// widget is on the board. Each page has its own schedule (2 minutes, 5 for the heavy feeds, never under the page's
/// max-age), with conditional requests (ETag, Last-Modified), ±10 % jitter, exponential backoff on failure (Retry-After
/// honored), at most 4 requests at once, and a refresh when the network comes back. The last readings are saved in the
/// plugin's folder, so a restart shows them at once.
/// </summary>
public static class StatusPoller
{
    private const int Parallel = 4;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinStale = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Slot> Slots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Subscription> Subscriptions = [];
    private static CancellationTokenSource? _loop;
    private static SemaphoreSlim _wake = new(0);
    private static string? _dataDirectory;
    private static bool _loaded;
    private static int _version;

    /// <summary>Changes whenever a reading (or an error) does: compare to know when to re-render.</summary>
    public static int Version => Volatile.Read(ref _version);

    /// <summary>The usual interval of a page: the heavy feeds change slowly and weigh up to 300 KB.</summary>
    public static TimeSpan BaseInterval(ProviderKind kind) =>
        kind is ProviderKind.Google or ProviderKind.Aws or ProviderKind.AzureRss or ProviderKind.Heroku ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(2);

    /// <summary>Without a successful read for 3 intervals (15 minutes at least), a page's state is unknown.</summary>
    public static TimeSpan StaleAfter(ProviderKind kind) => Max(BaseInterval(kind) * 3, MinStale);

    public static Snapshot? Get(string id)
    {
        lock (Gate)
            return Slots.TryGetValue(id, out var slot) ? slot.Snapshot() : null;
    }

    /// <summary>Polls these pages (if not yet); dispose to unsubscribe.</summary>
    public static IDisposable Subscribe(string dataDirectory, IReadOnlyCollection<CatalogEntry> entries)
    {
        var subscription = new Subscription(entries);
        lock (Gate)
        {
            _dataDirectory ??= dataDirectory;
            if (!_loaded)
            {
                Load();
                _loaded = true;
            }
            var now = DateTimeOffset.UtcNow;
            var stagger = 0;
            foreach (var entry in entries)
            {
                var slot = SlotFor(entry);
                slot.Subscribers++;
                if (slot.Reading is null && slot.NextDue > now + TimeSpan.FromSeconds(stagger * 0.3))
                    slot.NextDue = now + TimeSpan.FromSeconds(stagger++ * 0.3); // First reads soon, not all in the same instant.
            }
            Subscriptions.Add(subscription);
            if (_loop is null)
            {
                _loop = new CancellationTokenSource();
                _wake = new SemaphoreSlim(0);
                var token = _loop.Token;
                NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
                _ = Task.Run(() => LoopAsync(token));
            }
            else
            {
                _wake.Release();
            }
        }
        return subscription;
    }

    /// <summary>Reads a page now if it was never read (the settings editor, for its components); polled or not.</summary>
    public static async Task<Snapshot?> ReadOnceAsync(string dataDirectory, CatalogEntry entry, CancellationToken cancel)
    {
        Slot slot;
        lock (Gate)
        {
            _dataDirectory ??= dataDirectory;
            if (!_loaded)
            {
                Load();
                _loaded = true;
            }
            slot = SlotFor(entry);
            if (slot.Reading is not null || slot.Fetching)
                return slot.Snapshot();
            slot.Fetching = true;
        }
        await FetchAsync(slot, cancel);
        lock (Gate)
            return slot.Snapshot();
    }

    private static Slot SlotFor(CatalogEntry entry)
    {
        if (!Slots.TryGetValue(entry.Id, out var slot))
            Slots[entry.Id] = slot = new Slot(entry);
        else if (slot.Entry != entry)
        {
            // Same id, other address or kind (a user entry replaced): what was read no longer applies.
            if (slot.Entry.Url != entry.Url || slot.Entry.Kind != entry.Kind)
                slot.Reset();
            slot.Entry = entry;
        }
        return slot;
    }

    private static async Task LoopAsync(CancellationToken cancel)
    {
        using var gate = new SemaphoreSlim(Parallel);
        var running = new List<Task>();
        while (!cancel.IsCancellationRequested)
        {
            TimeSpan? wait = null;
            List<Slot> due;
            lock (Gate)
            {
                var now = DateTimeOffset.UtcNow;
                var active = Slots.Values.Where(s => s.Subscribers > 0 && !s.Fetching).ToList();
                due = active.Where(s => s.NextDue <= now).OrderBy(s => s.NextDue).ToList();
                if (due.Count == 0 && active.Count > 0)
                    wait = active.Min(s => s.NextDue) - now;
                foreach (var slot in due)
                    slot.Fetching = true;
            }
            if (due.Count == 0)
            {
                running.RemoveAll(t => t.IsCompleted);
                try
                {
                    var delay = wait is { } w ? Clamp(w, TimeSpan.FromMilliseconds(250), TimeSpan.FromHours(1)) : Timeout.InfiniteTimeSpan;
                    await _wake.WaitAsync(delay, cancel);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }
            foreach (var slot in due)
            {
                try
                {
                    await gate.WaitAsync(cancel);
                }
                catch (OperationCanceledException)
                {
                    lock (Gate)
                        foreach (var s in due.Where(s => s.Fetching))
                            s.Fetching = false;
                    return;
                }
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        await FetchAsync(slot, cancel);
                    }
                    finally
                    {
                        gate.Release();
                        lock (Gate)
                        {
                            if (_loop?.Token == cancel)
                                _wake.Release(); // Rescan: this page's next date moved.
                        }
                    }
                }, CancellationToken.None));
            }
        }
        try
        {
            await Task.WhenAll(running);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task FetchAsync(Slot slot, CancellationToken cancel)
    {
        CatalogEntry entry;
        Dictionary<string, CachedBody> cache;
        lock (Gate)
        {
            entry = slot.Entry;
            cache = new Dictionary<string, CachedBody>(slot.Cache);
        }
        var provider = Providers.For(entry.Kind);
        var urls = provider.Requests(entry);
        var bodies = new List<string>(urls.Count);
        var changed = false;
        TimeSpan? maxAge = null, retryAfter = null;
        string? error = null;
        var throttled = false;
        try
        {
            foreach (var url in urls)
            {
                cache.TryGetValue(url, out var cached);
                var result = await StatusHttp.GetAsync(url, cached?.ETag, cached?.LastModified, cancel);
                if (result.MaxAge is { } age)
                    maxAge = maxAge is { } m ? Max(m, age) : age;
                if (result.NotModified && cached is not null)
                {
                    bodies.Add(cached.Body);
                    continue;
                }
                if (result.Body is not { } body)
                {
                    error = result.Error ?? "No answer.";
                    retryAfter = result.RetryAfter;
                    throttled = result.Status == System.Net.HttpStatusCode.TooManyRequests;
                    break;
                }
                cache[url] = new CachedBody(result.ETag, result.LastModified, body.Body);
                bodies.Add(body.Body);
                changed = true;
            }
        }
        catch (OperationCanceledException)
        {
            lock (Gate)
                slot.Fetching = false;
            return;
        }

        ServiceReading? reading = null;
        if (error is null && (changed || slot.Reading is null))
        {
            try
            {
                reading = provider.Parse(entry, bodies);
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
            {
                error = "The page's answer couldn't be read: " + e.Message;
                cache.Clear(); // Don't answer "not modified" on a body that can't be read.
            }
        }

        var save = false;
        lock (Gate)
        {
            slot.Fetching = false;
            if (slot.Entry != entry)
                return; // Replaced meanwhile: its own fetch will come.
            var now = DateTimeOffset.UtcNow;
            slot.Cache = cache;
            if (error is null)
            {
                if (reading is not null)
                    slot.Reading = reading;
                slot.ReadAt = now;
                slot.Error = null;
                slot.Failures = 0;
                var interval = Max(slot.Interval, Max(maxAge ?? TimeSpan.Zero, MinInterval));
                slot.NextDue = now + Jitter(interval);
                save = reading is not null; // Saved on change only: a "not modified" only moves the time.
            }
            else
            {
                slot.Error = error;
                slot.Failures++;
                if (throttled)
                    slot.Interval = Min(slot.Interval * 2, MaxBackoff);
                var backoff = Min(TimeSpan.FromMinutes(Math.Pow(2, Math.Min(slot.Failures - 1, 5))), MaxBackoff);
                slot.NextDue = now + Max(backoff, Min(retryAfter ?? TimeSpan.Zero, MaxBackoff));
            }
            Interlocked.Increment(ref _version);
        }
        if (save)
            Save();
    }

    private static void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable)
            return;
        lock (Gate)
        {
            // Back online: what failed, or went stale, is read again now (staggered by the parallel limit).
            var now = DateTimeOffset.UtcNow;
            foreach (var slot in Slots.Values.Where(s => s.Subscribers > 0))
                if (slot.Failures > 0 || slot.ReadAt is not { } at || now - at > slot.Interval)
                    slot.NextDue = now;
            if (_loop is not null)
                _wake.Release();
        }
    }

    private static TimeSpan Jitter(TimeSpan interval) => interval * (0.9 + Random.Shared.NextDouble() * 0.2);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;

    private static string StatePath => Path.Combine(_dataDirectory ?? Path.GetTempPath(), "readings.json");

    private sealed record SavedReading(string Url, ProviderKind Kind, ServiceReading Reading, DateTimeOffset ReadAt);

    private static void Load()
    {
        try
        {
            if (!File.Exists(StatePath) || JsonSerializer.Deserialize<Dictionary<string, SavedReading>>(File.ReadAllText(StatePath), Json) is not { } saved)
                return;
            foreach (var (id, s) in saved)
            {
                if (Slots.ContainsKey(id) || s.Reading is null)
                    continue;
                var slot = new Slot(new CatalogEntry(id, id, s.Kind, s.Url, s.Url, StatusFilter.All))
                {
                    Reading = s.Reading,
                    ReadAt = s.ReadAt,
                    NextDue = DateTimeOffset.UtcNow, // Shown at once, read again soon.
                };
                Slots[id] = slot;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Unreadable: start without.
        }
    }

    private static void Save()
    {
        Dictionary<string, SavedReading> saved;
        lock (Gate)
        {
            saved = Slots.Values.Where(s => s.Reading is not null && s.ReadAt is not null)
                .ToDictionary(s => s.Entry.Id, s => new SavedReading(s.Entry.Url, s.Entry.Kind, s.Reading!, s.ReadAt!.Value));
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved, Json));
            File.Move(temp, StatePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not saved: the readings live on in memory.
        }
    }

    private sealed record CachedBody(string? ETag, string? LastModified, string Body);

    private sealed class Slot(CatalogEntry entry)
    {
        public CatalogEntry Entry { get; set; } = entry;
        public ServiceReading? Reading { get; set; }
        public DateTimeOffset? ReadAt { get; set; }
        public string? Error { get; set; }
        public int Failures { get; set; }
        public DateTimeOffset NextDue { get; set; } = DateTimeOffset.UtcNow;
        public TimeSpan Interval { get; set; } = BaseInterval(entry.Kind);
        public Dictionary<string, CachedBody> Cache { get; set; } = [];
        public bool Fetching { get; set; }
        public int Subscribers { get; set; }

        public Snapshot Snapshot() => new(Reading, ReadAt, Error, StaleAfter(Entry.Kind));

        public void Reset()
        {
            Reading = null;
            ReadAt = null;
            Error = null;
            Failures = 0;
            Cache = [];
            Interval = BaseInterval(Entry.Kind);
            NextDue = DateTimeOffset.UtcNow;
        }
    }

    private sealed class Subscription(IReadOnlyCollection<CatalogEntry> entries) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (Gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                Subscriptions.Remove(this);
                foreach (var entry in entries)
                    if (Slots.TryGetValue(entry.Id, out var slot) && slot.Subscribers > 0)
                        slot.Subscribers--;
                if (Subscriptions.Count > 0)
                    return;
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
                _loop?.Cancel();
                _loop = null;
            }
        }
    }
}
