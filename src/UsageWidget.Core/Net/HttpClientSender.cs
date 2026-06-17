using System.Net;

namespace UsageWidget.Core.Net;

/// <summary>
/// Production <see cref="IHttpSender"/> over <see cref="HttpClient"/>. Auto-redirects are disabled at
/// the handler level (contract #4 / v3.1). When a template opts into redirects, only SAME-HOST
/// redirects are followed — a cross-host redirect would escape the adapter allowlist, so it is not
/// followed automatically. Headers are added without validation so the full captured browser header
/// set (R1) can be replayed verbatim.
/// </summary>
public sealed class HttpClientSender : IHttpSender, IDisposable
{
    private const int MaxRedirects = 5;
    private readonly HttpClient _client;

    public HttpClientSender(HttpClient? client = null)
    {
        if (client is not null)
        {
            _client = client;
            return;
        }

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _client = new HttpClient(handler);
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

    private static HttpRequestMessage BuildRequest(
        string method, Uri url, IReadOnlyDictionary<string, string> headers, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
        {
            request.Content = new StringContent(body);
        }

        foreach (var (name, value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, value);
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
