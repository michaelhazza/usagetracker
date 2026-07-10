using UsageWidget.Core.Security;

namespace UsageWidget.Core.Net;

/// <summary>
/// Decorator over <see cref="IHttpSender"/> that retries TRANSIENT failures within one refresh,
/// so a single dropped packet, DNS blip, or proxy hiccup doesn't mark an account failed for a
/// whole polling cycle. Retryable: thrown transport errors (DNS, connect, reset, timeout) and
/// 408/5xx responses. Never retried: 2xx–4xx answers (they are real), 429 (backoff owns that),
/// and anything that looks like an anti-bot challenge (retrying a challenge only escalates it).
/// Each attempt gets the full per-request timeout, preserving per-account isolation (contract #5).
/// </summary>
public sealed class RetryingHttpSender : IHttpSender
{
    private readonly IHttpSender _inner;
    private readonly int _attempts;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RetryingHttpSender(
        IHttpSender inner,
        int attempts = 2,
        Func<int, TimeSpan>? backoff = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _inner = inner;
        // Clamp the ceiling too: a hand-edited config could otherwise set hundreds of attempts,
        // and since each holds the cycle gate, one dead endpoint would freeze the whole widget.
        _attempts = Math.Clamp(attempts, 1, 5);
        _backoff = backoff ?? DefaultBackoff;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
    }

    /// <summary>
    /// Grows ~0.5 s, ~1 s, … with up to 250 ms of jitter (so parallel accounts desynchronize),
    /// capped at 5 s so total in-cycle retry time stays bounded.
    /// </summary>
    private static TimeSpan DefaultBackoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(500 * (attempt + 1), 5000) + Random.Shared.Next(0, 250));

    public async Task<HttpResponseData> SendAsync(
        string method,
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        bool followRedirects,
        TimeSpan timeout,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var lastAttempt = attempt + 1 >= _attempts;
            try
            {
                var response = await _inner
                    .SendAsync(method, url, headers, body, followRedirects, timeout, ct)
                    .ConfigureAwait(false);

                if (lastAttempt ||
                    !IsRetryable(response) ||
                    ChallengeDetector.IsChallenge(response.ContentType, response.Body))
                {
                    return response;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller cancelled (shutdown) — never swallow into a retry
            }
            catch (Exception) when (!lastAttempt)
            {
                // Transient transport fault (timeout, DNS, reset) — fall through to the retry delay.
            }

            await _delay(_backoff(attempt), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Mirrors <see cref="ResponseClassifier"/>'s "server-side trouble is transient" rule: 408 and
    /// EVERY 5xx. Cloudflare's origin errors (520–526) arrive constantly in front of these
    /// providers — enumerating specific codes silently excluded them from the in-cycle retry.
    /// </summary>
    private static bool IsRetryable(HttpResponseData response) =>
        response.StatusCode == 408 || response.StatusCode >= 500;
}
