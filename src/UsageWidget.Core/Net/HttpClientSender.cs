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
///   - Pooled connections are recycled on a 5-minute lifetime, so DNS updates and half-dead sockets
///     resolve themselves without wedging the widget hours into a run (each recycle re-resolves DNS
///     and opens a fresh connection).
///   - Transport-level headers from a capture (Accept-Encoding, Host, Content-Length, …) are
///     stripped: they describe the browser's original transport, not this one. In particular a
///     browser's "Accept-Encoding: … zstd" would make the server reply with compression .NET 8
///     cannot decode, turning every response into parse garbage.
///
/// KNOWN LIMITATION: .NET reads the system (WinInet/IE) proxy configuration once per process and
/// caches it (<see cref="HttpClient.DefaultProxy"/>); there is no supported in-process way to force a
/// re-read. If the user connects/disconnects a VPN or changes their proxy while the widget is
/// running, they must restart it. Authenticated proxies (407) ARE handled via
/// <see cref="SocketsHttpHandler.DefaultProxyCredentials"/>.
/// </summary>
public sealed class HttpClientSender : IHttpSender, IDisposable
{
    private const int MaxRedirects = 5;
    private readonly HttpClient _client;

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
        _client = client ?? BuildClient();
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

            // Recycle pooled connections so a long-running widget re-resolves DNS and drops
            // half-dead sockets instead of re-using a connection to a dead endpoint forever.
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

        var current = url;
        for (var hop = 0; ; hop++)
        {
            using var request = BuildRequest(method, current, headers, body);
            using var response = await _client
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
                // TryAddWithoutValidation also returns false for a malformed header NAME (a space,
                // empty), and HttpHeaders.Remove VALIDATES the name and throws — so a single bad
                // header in a hand-edited template would blow up every request. Contain it and
                // skip the offending header, matching the old lenient behavior.
                try
                {
                    content.Headers.Remove(name);
                    content.Headers.TryAddWithoutValidation(name, value);
                }
                catch (FormatException) { /* invalid header name — skip it */ }
                catch (ArgumentException) { /* empty header name — skip it */ }
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
