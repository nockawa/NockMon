using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace ServiceStatus.NockMon.Status;

/// <summary>The outcome of a GET: a body, "not modified", or a failure (with a server-requested delay, if any).</summary>
public sealed record FetchResult(
    HttpStatusCode? Status,
    Fetched? Body,
    string? ETag,
    string? LastModified,
    TimeSpan? MaxAge,
    TimeSpan? RetryAfter,
    string? Error)
{
    public bool NotModified => Status == HttpStatusCode.NotModified;
}

/// <summary>
/// HTTP for status pages, inside NockMon's elevated process: https only, no cookies or credentials, decompressed,
/// at most 2 MB (decompressed) per body, 3 redirects, and no connection to loopback or link-local addresses (a user-added
/// URL can't reach the machine itself or a VM's metadata service; checked on the resolved address, so DNS can't dodge it).
/// The system proxy, if any, is used.
/// </summary>
public static class StatusHttp
{
    public const int MaxBody = 2 << 20;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        UseProxy = true,
        Credentials = null,
        PreAuthenticate = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectCallback = ConnectAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = MaxBody,
        DefaultRequestHeaders = { { "User-Agent", "NockMon-ServiceStatus/1.0 (+https://github.com/nockawa/NockMon)" } },
    };

    public static async Task<FetchResult> GetAsync(string url, string? etag, string? lastModified, CancellationToken cancel)
    {
        if (!Catalog.IsHttps(url))
            return Failed("Not an https address.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json, application/rss+xml, application/xml;q=0.9, */*;q=0.5");
            if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag))
                request.Headers.IfNoneMatch.Add(tag);
            if (lastModified is not null && DateTimeOffset.TryParse(lastModified, out var since))
                request.Headers.IfModifiedSince = since;
            using var response = await Client.SendAsync(request, cancel);
            var maxAge = response.Headers.CacheControl?.MaxAge;
            var retryAfter = response.Headers.RetryAfter is { } r
                ? r.Delta ?? (r.Date is { } d ? d - DateTimeOffset.UtcNow : null)
                : null;
            if (response.StatusCode == HttpStatusCode.NotModified)
                return new FetchResult(response.StatusCode, null, etag, lastModified, maxAge, null, null);
            if (!response.IsSuccessStatusCode)
                return new FetchResult(response.StatusCode, null, null, null, null, retryAfter, $"HTTP {(int)response.StatusCode}");
            // HTTPS to the end: no redirect may land on http (the handler refuses https→http, this states it).
            if (response.RequestMessage?.RequestUri is not { Scheme: "https" } final)
                return Failed("Redirected off https.");
            var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
            var type = response.Content.Headers.ContentType;
            return new FetchResult(response.StatusCode, new Fetched(final, type?.MediaType, Decode(bytes, type?.CharSet)),
                response.Headers.ETag?.ToString(), response.Content.Headers.LastModified?.ToString("R"), maxAge, null, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            if (cancel.IsCancellationRequested)
                throw new OperationCanceledException(cancel);
            return Failed(e is TaskCanceledException ? "Timed out." : e.Message);
        }
    }

    /// <summary>Text by the declared charset (AWS serves UTF-16), a byte order mark winning; UTF-8 otherwise.</summary>
    public static string Decode(byte[] bytes, string? charset)
    {
        Encoding encoding;
        try
        {
            encoding = charset is { Length: > 0 } ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8;
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }
        using var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Loopback, link-local (169.254/16 and fe80::/10, where cloud metadata services live) and unspecified.</summary>
    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal;
        var b = address.GetAddressBytes();
        return b[0] == 169 && b[1] == 254 || b[0] == 0;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancel);
        // Through a proxy (the endpoint isn't the request's host), the proxy may well be local: only direct connections
        // to the page are checked.
        var direct = string.Equals(context.DnsEndPoint.Host, context.InitialRequestMessage.RequestUri?.IdnHost, StringComparison.OrdinalIgnoreCase);
        var allowed = direct ? addresses.Where(a => !IsBlocked(a)).ToArray() : addresses;
        if (allowed.Length == 0)
            throw new HttpRequestException($"{context.DnsEndPoint.Host} is a local address.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancel);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static FetchResult Failed(string error) => new(null, null, null, null, null, null, error);
}
