using System.Net;

namespace UsageWidget.Core.Net;

/// <summary>
/// Production <see cref="IHttpSender"/> over <see cref="HttpClient"/>. Auto-redirects are disabled at
/// the handler level (contract #4 / v3.1). When a template opts into redirects, only SAME-HOST
/// redirects are followed — a cross-host redirect would escape the adapter allowlist, so it is not
/// followed automatically. Headers are added without validation so the full captured browser header
/// set (R1) can be replayed verbatim.
///
/// Robustness contract for a long-running multi-account tray app:
///   - Cookies are NEVER managed by a container. Each account replays its own pasted Cookie header;
///     a shared container would capture one account's Set-Cookie (Cloudflare rotates cookies
///     constantly) and attach it to every other account's requests on the same host — silently
///     corrupting or cross-contaminating sessions.
///   - Pooled connections are recycled so DNS updates, proxy failover, and half-dead sockets can't
///     wedge the widget hours into a run.
///   - Transport-level headers from a capture (Accept-Encoding, Host, Content-Length, …) are
///     stripped: they describe the browser's original transport, not this one. In particular a
///     browser's "Accept-Encoding: … zstd" would make the server reply with compression .NET 8
///     cannot decode, turning every response into parse garbage.
/// </summary>
public sealed class HttpClientSender : IHttpSender, IDisposable
{
    private const int MaxRedirects = 5;

    /// <summary>
    /// How long one HttpClient (and its handler) lives before being rebuilt. On Windows, .NET
    /// snapshots the system (IE/WinInet) proxy settings when a handler first uses them and caches
    /// them for its lifetime — so without rotation, connecting/disconnecting a VPN or changing the
    /// proxy breaks EVERY account until the app is restarted.
    /// </summary>
    private static readonly TimeSpan ClientLifetime = TimeSpan.FromMinutes(15);

    private readonly object _swapLock = new();
    private readonly bool _externalClient;
    private HttpClient _client;
    private DateTimeOffset _clientBuiltAt;

    /// <summary>
    /// Headers that must never be replayed verbatim because they describe the capturing browser's
    /// transport, not ours. <see cref="Import.CurlAccountImport"/> strips these at import time too;
    /// this is fail-safe for hand-edited templates and old configs.
    /// </summary>
    private static readonly HashSet<string> TransportHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept-encoding",  // negotiated by AutomaticDecompression (gzip/deflate/br — zstd is NOT decodable)
        "host",             // derived from the request URL
        "content-length",   // derived from the actual body
        "connection",
        "keep-alive",
        "transfer-encoding",
        "te",
        "upgrade",
        "expect",
        "proxy-connection",
        "proxy-authorization",
    };

    public HttpClientSender(HttpClient? client = null)
    {
        if (client is not null)
        {
            _client = client;
            _externalClient = true;
            _clientBuiltAt = DateTimeOffset.UtcNow;
            return;
        }

        _client = BuildClient();
        _clientBuiltAt = DateTimeOffset.UtcNow;
    }

    private static HttpClient BuildClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,

            // Replay the pasted Cookie header verbatim; see class remarks for why a cookie
            // container must never be enabled here.
            UseCookies = false,

            // Recycle pooled connections so a long-running widget observes DNS changes and proxy
            // failover instead of re-using a connection to a dead endpoint forever.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),

            // Don't funnel every account through ONE multiplexed HTTP/2 connection: a single
            // connection-level reset would fail all accounts in the same cycle at once.
            EnableMultipleHttp2Connections = true,

            // Authenticated corporate proxies (407): answer with the signed-in user's credentials.
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };

        return new HttpClient(handler)
        {
            // The per-request CancellationTokenSource below is the single timeout authority; the
            // default 100 s HttpClient.Timeout would otherwise race it with a confusing error.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Returns the live client, rotating it after <see cref="ClientLifetime"/> so cached system
    /// proxy settings get re-read. The retired client is disposed on a delay long enough for any
    /// in-flight request (10 s timeout × retries) to complete on it safely.
    /// </summary>
    private HttpClient CurrentClient()
    {
        if (_externalClient) return _client;

        lock (_swapLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _clientBuiltAt > ClientLifetime)
            {
                var retired = _client;
                _client = BuildClient();
                _clientBuiltAt = now;
                _ = Task.Delay(TimeSpan.FromMinutes(2))
                    .ContinueWith(_ => retired.Dispose(), TaskScheduler.Default);
            }

            return _client;
        }
    }

    public async Task<HttpResponseData> SendAsync(
        string method,
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        bool followRedirects,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var client = CurrentClient();
        var current = url;
        for (var hop = 0; ; hop++)
        {
            using var request = BuildRequest(method, current, headers, body);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);

            var isRedirect = (int)response.StatusCode is >= 300 and < 400;
            if (followRedirects && isRedirect && hop < MaxRedirects &&
                response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);

                // Same-host only: a cross-host redirect would bypass the allowlist, so stop here.
                if (string.Equals(next.Host, current.Host, StringComparison.OrdinalIgnoreCase))
                {
                    current = next;
                    continue;
                }
            }

            return await ToResponseDataAsync(response).ConfigureAwait(false);
        }
    }

    internal static HttpRequestMessage BuildRequest(
        string method, Uri url, IReadOnlyDictionary<string, string> headers, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url)
        {
            // Browsers speak HTTP/2 to these endpoints; a browser header set arriving over
            // HTTP/1.1 is a bot-fingerprinting mismatch. Downgrades automatically when the
            // server (or a proxy) can't do h2.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        if (body is not null)
        {
            request.Content = new StringContent(body);
        }

        foreach (var (name, value) in headers)
        {
            if (TransportHeaders.Contains(name)) continue;

            if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content is { } content)
            {
                // A content header (e.g. Content-Type) must REPLACE the StringContent default —
                // appending would send "text/plain; charset=utf-8, application/json".
                content.Headers.Remove(name);
                content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return request;
    }

    private static async Task<HttpResponseData> ToResponseDataAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in response.Headers)
        {
            headers[k] = string.Join(", ", v);
        }

        foreach (var (k, v) in response.Content.Headers)
        {
            headers[k] = string.Join(", ", v);
        }

        return new HttpResponseData((int)response.StatusCode, contentType, content, headers);
    }

    public void Dispose() => _client.Dispose();
}
