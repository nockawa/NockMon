using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeUsage.NockMon;

/// <summary>The weekly (7-day) window: utilization in % and when it resets.</summary>
public sealed record WeeklyWindow(double UtilPct, DateTimeOffset ResetsAt);

/// <summary>Burn rate over the trailing 24 h, in %/day; <see cref="HoursObserved"/> is the history behind it.</summary>
public sealed record Pace(double PctPerDay, double HoursObserved);

/// <summary>What the widgets show: the latest successful reading (kept across errors) and the latest error.</summary>
public sealed record UsageState(WeeklyWindow? Weekly, Pace? Pace, DateTimeOffset? FetchedAt, string? Error, DateTimeOffset NextAttempt);

/// <summary>
/// Shared by every weekly widget: one poller for the whole app, running while at least one widget is on the board.
/// Same source as claude-usage: the OAuth token the Claude CLI stored, and the (unofficial) usage endpoint. When
/// claude-usage itself runs, its fresh cache is read instead of calling the API twice. Its 24 h history (for the pace)
/// seeds this one at start.
/// </summary>
public static class UsageService
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private static readonly TimeSpan PaceLookback = TimeSpan.FromHours(24);
    private static readonly Lock Gate = new();
    private static readonly HttpClient Http = CreateClient();
    private static readonly List<Subscription> Subscribers = [];
    private static CancellationTokenSource? _loop;
    private static SemaphoreSlim _wake = new(0);
    private static volatile UsageState _state = new(null, null, null, null, DateTimeOffset.UtcNow);
    private static List<Sample>? _history;
    private static string? _dataDirectory;

    public static UsageState State => _state;

    /// <summary>Starts polling (if not yet) at the shortest interval among subscribers; dispose to unsubscribe.</summary>
    public static IDisposable Subscribe(string dataDirectory, TimeSpan interval)
    {
        var subscription = new Subscription(interval);
        lock (Gate)
        {
            _dataDirectory ??= dataDirectory;
            Subscribers.Add(subscription);
            if (_loop is null)
            {
                _loop = new CancellationTokenSource();
                _wake = new SemaphoreSlim(0);
                var token = _loop.Token;
                _ = Task.Run(() => LoopAsync(token));
            }
            else
            {
                _wake.Release(); // A shorter interval may apply now.
            }
        }
        return subscription;
    }

    /// <summary>Fetch now (the widget's refresh button).</summary>
    public static void RefreshNow()
    {
        lock (Gate)
            _wake.Release();
    }

    private static TimeSpan Interval
    {
        get
        {
            lock (Gate)
                return Subscribers.Count == 0 ? TimeSpan.FromMinutes(10) : Subscribers.Min(s => s.Interval);
        }
    }

    private static async Task LoopAsync(CancellationToken cancel)
    {
        var failures = 0;
        while (!cancel.IsCancellationRequested)
        {
            var interval = Interval;
            TimeSpan delay;
            try
            {
                var (weekly, fetchedAt) = await ReadAsync(interval, cancel);
                failures = 0;
                var history = Record(weekly, fetchedAt);
                Publish(_state with { Weekly = weekly, Pace = ComputePace(history, weekly, fetchedAt), FetchedAt = fetchedAt, Error = null, NextAttempt = fetchedAt + interval });
                delay = fetchedAt + interval - DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                failures++;
                delay = RetryDelay(e, failures, interval);
                Publish(_state with { Error = Describe(e), NextAttempt = DateTimeOffset.UtcNow + delay });
            }

            try
            {
                await _wake.WaitAsync(delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay, cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static void Publish(UsageState state) => _state = state;

    /// <summary>claude-usage's cache when it is fresher than an interval, the API otherwise.</summary>
    private static async Task<(WeeklyWindow Weekly, DateTimeOffset FetchedAt)> ReadAsync(TimeSpan interval, CancellationToken cancel)
    {
        if (TryReadClaudeUsageCache() is { } cached && DateTimeOffset.UtcNow - cached.FetchedAt < interval
            && (_state.FetchedAt is not { } ours || cached.FetchedAt > ours))
            return cached;

        var token = ReadToken() ?? throw new UsageException(
            "No Claude credentials: run `claude login`, or set CLAUDE_CODE_OAUTH_TOKEN.");
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new("Bearer", token);
        using var response = await Http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode) { Data = { ["RetryAfter"] = response.Headers.RetryAfter?.Delta } };
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        return (ParseWeekly(doc.RootElement) ?? throw new UsageException("The usage response has no weekly window (plan without weekly limits?)."),
            DateTimeOffset.UtcNow);
    }

    /// <summary><c>seven_day</c> of the usage response; null when absent or without numbers (some Team/Enterprise plans).</summary>
    public static WeeklyWindow? ParseWeekly(JsonElement root)
    {
        if (!root.TryGetProperty("seven_day", out var w) || w.ValueKind != JsonValueKind.Object)
            return null;
        if (!w.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number)
            return null;
        if (!w.TryGetProperty("resets_at", out var r) || r.ValueKind != JsonValueKind.String)
            return null;
        return new WeeklyWindow(u.GetDouble(), DateTimeOffset.Parse(r.GetString()!, CultureInfo.InvariantCulture));
    }

    private static (WeeklyWindow Weekly, DateTimeOffset FetchedAt)? TryReadClaudeUsageCache()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsage", "cache.json");
            if (!File.Exists(path))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var fetchedAt = DateTimeOffset.Parse(doc.RootElement.GetProperty("fetched_at").GetString()!, CultureInfo.InvariantCulture);
            return ParseWeekly(doc.RootElement.GetProperty("body")) is { } weekly ? (weekly, fetchedAt) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Same order as the Claude CLI on Windows/Linux: the environment variable, then ~/.claude/.credentials.json.</summary>
    private static string? ReadToken()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            if (!File.Exists(path))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString();
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    // ---- History and pace (same rules as claude-usage) -------------------------------------------------------

    private sealed record Sample(DateTimeOffset At, double Util, DateTimeOffset ResetsAt);

    /// <summary>The API jitters resets_at by milliseconds; a rollover moves it by a whole week.</summary>
    private static bool SameWindow(DateTimeOffset a, DateTimeOffset b) => Math.Abs((a - b).TotalMinutes) < 10;

    private static List<Sample> Record(WeeklyWindow weekly, DateTimeOffset at)
    {
        _history ??= LoadHistory();
        if (_history.Count == 0 || _history[^1].At < at)
            _history.Add(new Sample(at, weekly.UtilPct, weekly.ResetsAt));
        _history.RemoveAll(s => !SameWindow(s.ResetsAt, weekly.ResetsAt) || s.At < at - PaceLookback - TimeSpan.FromHours(1));
        SaveHistory(_history);
        return _history;
    }

    /// <summary>
    /// Burn over the trailing 24 h, from the last sample at or before the cutoff. Null with less than an hour of
    /// history: the widget then shows the average since the window opened.
    /// </summary>
    private static Pace? ComputePace(List<Sample> history, WeeklyWindow weekly, DateTimeOffset now)
    {
        var current = history.Where(s => SameWindow(s.ResetsAt, weekly.ResetsAt)).OrderBy(s => s.At).ToList();
        if (current.Count < 2)
            return null;
        var cutoff = now - PaceLookback;
        var earliest = current.LastOrDefault(s => s.At <= cutoff) ?? current[0];
        var latest = current[^1];
        var hours = (latest.At - earliest.At).TotalHours;
        return hours < 1 ? null : new Pace(Math.Max(0, (latest.Util - earliest.Util) / hours * 24), hours);
    }

    private static string HistoryPath => Path.Combine(_dataDirectory ?? Path.GetTempPath(), "weekly-history.json");

    /// <summary>Ours, merged with claude-usage's weekly series when present (pace available at once).</summary>
    private static List<Sample> LoadHistory()
    {
        var samples = new List<Sample>();
        Read(HistoryPath, root => root);
        Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsage", "history.json"),
            root => root["series"]?["weekly"]);
        return samples.DistinctBy(s => s.At).OrderBy(s => s.At).ToList();

        void Read(string path, Func<JsonNode, JsonNode?> select)
        {
            try
            {
                if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not { } root || select(root) is not JsonArray items)
                    return;
                foreach (var item in items)
                {
                    if (item is null)
                        continue;
                    samples.Add(new Sample(
                        DateTimeOffset.Parse((string)item["at"]!, CultureInfo.InvariantCulture),
                        (double)item["util"]!,
                        DateTimeOffset.Parse((string)item["resetsAt"]!, CultureInfo.InvariantCulture)));
                }
            }
            catch (Exception e) when (e is IOException or JsonException or FormatException or InvalidOperationException or NullReferenceException)
            {
                // Unreadable history: start without it.
            }
        }
    }

    private static void SaveHistory(List<Sample> samples)
    {
        try
        {
            var array = new JsonArray(samples.Select(s => (JsonNode)new JsonObject
            {
                ["at"] = s.At.ToString("o", CultureInfo.InvariantCulture),
                ["util"] = s.Util,
                ["resetsAt"] = s.ResetsAt.ToString("o", CultureInfo.InvariantCulture),
            }).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(HistoryPath, array.ToJsonString());
        }
        catch (IOException)
        {
        }
    }

    // ---- Errors -----------------------------------------------------------------------------------------------

    private static string Describe(Exception e) => e switch
    {
        UsageException => e.Message,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            "Not authorized: the Claude CLI token expired or was revoked. Run `claude` (or `claude login`) to refresh it.",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "Rate limited by the usage endpoint; retrying later.",
        HttpRequestException { StatusCode: { } status } => $"Usage endpoint error: HTTP {(int)status}.",
        TaskCanceledException => "The usage request timed out.",
        _ => $"Cannot read the usage: {e.Message}",
    };

    /// <summary>Exponential backoff from a base per error kind, Retry-After honored, capped at the interval.</summary>
    private static TimeSpan RetryDelay(Exception e, int attempt, TimeSpan cap)
    {
        if (e is HttpRequestException { Data: var data } && data["RetryAfter"] is TimeSpan hint)
            return hint < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : hint > cap ? cap : hint;
        var baseSeconds = e switch
        {
            UsageException => 60.0,
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => 60.0,
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => 30.0,
            _ => 10.0,
        };
        var seconds = Math.Min(baseSeconds * Math.Pow(2, Math.Min(attempt - 1, 8)), cap.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        }) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("anthropic-beta", "oauth-2025-04-20");
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        return client;
    }

    private sealed class UsageException(string message) : Exception(message);

    private sealed class Subscription(TimeSpan interval) : IDisposable
    {
        public TimeSpan Interval { get; } = interval;

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
