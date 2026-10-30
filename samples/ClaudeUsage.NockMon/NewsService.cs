using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeUsage.NockMon;

/// <summary>
/// Claude and Anthropic news, shared by every widget: one poller (every 30 min) fetching only the sources the widgets'
/// categories need, and one read set, persisted in the plugin's folder. The first fetch of a source marks everything
/// already published as read, so a new install doesn't light up with months of history.
/// </summary>
public static class NewsService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    private const int MaxRead = 1000;

    private enum Source { ReleaseNotes, WhatsNew, Changelog }

    private static readonly Lock Gate = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly List<Subscription> Subscribers = [];
    private static readonly Dictionary<Source, List<NewsItem>> Items = [];
    private static readonly HashSet<string> Read = [];
    private static readonly List<string> ReadOrder = [];
    private static readonly HashSet<Source> Baselined = [];
    private static CancellationTokenSource? _loop;
    private static SemaphoreSlim _wake = new(0);
    private static string? _dataDirectory;
    private static bool _loaded;
    private static volatile IReadOnlyList<NewsItem> _unread = [];

    /// <summary>Every unread item, newest first. Replaced as a whole on change: compare references to detect one.</summary>
    public static IReadOnlyList<NewsItem> Unread => _unread;

    /// <summary>Polls (if not yet) for these categories, among others; dispose to unsubscribe.</summary>
    public static IDisposable Subscribe(string dataDirectory, IReadOnlySet<NewsCategory> categories)
    {
        var subscription = new Subscription(categories);
        lock (Gate)
        {
            _dataDirectory ??= dataDirectory;
            if (!_loaded)
            {
                Load();
                _loaded = true;
            }
            Subscribers.Add(subscription);
            if (_loop is null)
            {
                _loop = new CancellationTokenSource();
                _wake = new SemaphoreSlim(0);
                var token = _loop.Token;
                _ = Task.Run(() => LoopAsync(token));
            }
            else if (NeededSources().Any(s => !Items.ContainsKey(s)))
            {
                _wake.Release(); // A category whose source was never fetched.
            }
        }
        return subscription;
    }

    public static void MarkRead(IEnumerable<NewsItem> items)
    {
        lock (Gate)
        {
            var changed = false;
            foreach (var item in items)
                changed |= Remember(item.Key);
            if (!changed)
                return;
            Save();
            Publish();
        }
    }

    private static HashSet<Source> NeededSources()
    {
        var categories = Subscribers.SelectMany(s => s.Categories).ToHashSet();
        var sources = new HashSet<Source>();
        if (categories.Contains(NewsCategory.ClaudeCode))
            sources.Add(Source.WhatsNew);
        if (categories.Contains(NewsCategory.ClaudeCodeReleases))
            sources.Add(Source.Changelog);
        if (categories.Any(c => c is not (NewsCategory.ClaudeCode or NewsCategory.ClaudeCodeReleases)))
            sources.Add(Source.ReleaseNotes);
        return sources;
    }

    private static async Task LoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            HashSet<Source> sources;
            lock (Gate)
                sources = NeededSources();
            var results = await Task.WhenAll(sources.Select(async s => (Source: s, Items: await FetchAsync(s, cancel))));
            if (cancel.IsCancellationRequested)
                return;
            lock (Gate)
            {
                foreach (var (source, items) in results)
                {
                    // Nothing parsed: the page failed or changed shape. Keep what we had rather than drop or replay items.
                    if (items is not { Count: > 0 })
                        continue;
                    Items[source] = items;
                    if (Baselined.Add(source))
                        foreach (var item in items)
                            Remember(item.Key);
                }
                Save();
                Publish();
            }

            try
            {
                await _wake.WaitAsync(results.Any(r => r.Items is not { Count: > 0 }) ? RetryDelay : Interval, cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task<List<NewsItem>?> FetchAsync(Source source, CancellationToken cancel)
    {
        try
        {
            var (url, parse) = source switch
            {
                Source.ReleaseNotes => (NewsFeeds.ReleaseNotesUrl + ".md", (Func<string, List<NewsItem>>)(md => NewsFeeds.ParseReleaseNotes(md))),
                Source.WhatsNew => (NewsFeeds.WhatsNewUrl + "/index.md", md => NewsFeeds.ParseWhatsNew(md)),
                _ => (NewsFeeds.ChangelogUrl + ".md", md => NewsFeeds.ParseChangelog(md)),
            };
            return parse(await Http.GetStringAsync(url, cancel));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static bool Remember(string key)
    {
        if (!Read.Add(key))
            return false;
        ReadOrder.Add(key);
        if (ReadOrder.Count > MaxRead)
        {
            foreach (var old in ReadOrder.Take(ReadOrder.Count - MaxRead))
                Read.Remove(old);
            ReadOrder.RemoveRange(0, ReadOrder.Count - MaxRead);
        }
        return true;
    }

    private static void Publish() =>
        _unread = Items.Values.SelectMany(l => l).Where(i => !Read.Contains(i.Key)).OrderByDescending(i => i.Date).ToArray();

    private static string StatePath => Path.Combine(_dataDirectory ?? Path.GetTempPath(), "news.json");

    private static void Load()
    {
        try
        {
            if (!File.Exists(StatePath) || JsonNode.Parse(File.ReadAllText(StatePath)) is not JsonObject root)
                return;
            foreach (var source in root["baselined"]?.AsArray() ?? [])
                if (Enum.TryParse<Source>((string?)source, out var s))
                    Baselined.Add(s);
            foreach (var key in root["read"]?.AsArray() ?? [])
                if ((string?)key is { } k)
                    Remember(k);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            // Unreadable: start over, re-baselining every source rather than replaying it.
            Baselined.Clear();
            Read.Clear();
            ReadOrder.Clear();
        }
    }

    private static void Save()
    {
        try
        {
            var root = new JsonObject
            {
                ["baselined"] = new JsonArray(Baselined.Select(s => (JsonNode)s.ToString()).ToArray()),
                ["read"] = new JsonArray(ReadOrder.Select(k => (JsonNode)k).ToArray()),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, root.ToJsonString());
        }
        catch (IOException)
        {
        }
    }

    private sealed class Subscription(IReadOnlySet<NewsCategory> categories) : IDisposable
    {
        public IReadOnlySet<NewsCategory> Categories { get; } = categories;

        public void Dispose()
        {
            lock (Gate)
            {
                if (!Subscribers.Remove(this) || Subscribers.Count > 0)
                    return;
                _loop?.Cancel();
                _loop = null;
            }
        }
    }
}
