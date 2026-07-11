using UsageWidget.Core.Accounts;
using UsageWidget.Core.Model;
using UsageWidget.Core.Net;
using UsageWidget.Core.Security;
using UsageWidget.Core.Templating;

namespace UsageWidget.Core.Adapters;

/// <summary>
/// Generic, fully config-driven adapter that works for every replay-based source
/// (ClaudeWebToken, Codex*, AnthropicApiKey). The request shape lives entirely in the
/// <see cref="RequestTemplate"/>; nothing here is provider-specific.
///
/// Flow (order matters — contract #4):
///   1. allowlist check on the destination host  → fail closed before anything is sent
///   2. inject the secret into headers/body       → only after the host cleared the allowlist
///   3. send (timeout + redirects-off honored)
///   4. classify (challenge → 401/403 → 429 → 2xx → 5xx → other)
///   5. map, and downgrade an empty 200 to ParseFailed (§3 validation)
/// All failures become a failure <see cref="UsageResult"/> with a redacted message.
/// </summary>
public sealed class TemplateAdapter : IProviderAdapter
{
    private readonly MappingEngine _mapping;
    private readonly TimeSpan _timeout;

    public TemplateAdapter(AccountSource source, MappingEngine mapping, TimeSpan? timeout = null)
    {
        Source = source;
        _mapping = mapping;
        _timeout = timeout ?? TimeSpan.FromSeconds(10); // contract #5 default
    }

    public AccountSource Source { get; }

    public async Task<UsageResult> FetchAsync(
        Account account,
        RequestTemplate template,
        string secret,
        IHttpSender sender,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var label = account.DisplayLabel(null);

        // (1) Fail-closed allowlist BEFORE any credential injection or network I/O.
        if (!Uri.TryCreate(template.Url, UriKind.Absolute, out var url) ||
            !HostnameAllowlist.IsAllowed(url, template.AllowedHosts))
        {
            var host = url?.Host ?? "(unparseable url)";
            return UsageResult.Failure(
                RefreshErrorKind.ParseFailed, label,
                $"Request blocked: host '{host}' is not allowlisted.", now);
        }

        // (2) Inject the secret only now that the host is trusted.
        var headers = TemplateEngine.InjectHeaders(template.Headers, secret);
        var body = TemplateEngine.InjectBody(template.Body, secret);

        // (3) Send, isolating transport faults as NetworkTimeout (contract #5).
        HttpResponseData response;
        try
        {
            response = await sender
                .SendAsync(template.Method, url, headers, body, template.FollowRedirects, _timeout, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App shutdown / cycle cancellation — propagate; a bogus "timed out" result would be a lie.
            throw;
        }
        catch (OperationCanceledException)
        {
            return UsageResult.Failure(
                RefreshErrorKind.NetworkTimeout, label,
                $"No answer within {(int)_timeout.TotalSeconds}s — will retry.", now);
        }
        catch (Exception ex)
        {
            return UsageResult.Failure(
                RefreshErrorKind.NetworkTimeout, label, TransportError.Describe(ex), now);
        }

        // (4) Classify.
        if (ResponseClassifier.Classify(response) is { } error)
        {
            return UsageResult.Failure(error.Kind, label, error.Message, now);
        }

        // (5) Map and validate.
        try
        {
            var extracted = _mapping.Extract(response.Body, response.Headers, template.Mappings, now);
            var resolvedLabel = account.DisplayLabel(extracted.Identity);
            var result = UsageResult.Success(resolvedLabel, extracted.Session, extracted.Weekly, now);

            if (result.HasUsableData) return result;

            return LooksLikeHtml(response)
                ? HtmlInsteadOfData(resolvedLabel, now)
                : UsageResult.Failure(
                    RefreshErrorKind.ParseFailed, resolvedLabel,
                    "Response parsed but no usage fields matched the configured mappings.", now);
        }
        catch (MappingException)
        {
            return LooksLikeHtml(response)
                ? HtmlInsteadOfData(label, now)
                : UsageResult.Failure(
                    RefreshErrorKind.ParseFailed, label,
                    "Response was not valid JSON for the configured mappings.", now);
        }
    }

    /// <summary>
    /// A 200 whose body is a web page instead of usage data is almost always the provider serving
    /// its login page to a dead session — an auth problem, not the "config problem" that
    /// ParseFailed would tell the user to debug in the template editor.
    /// </summary>
    private static bool LooksLikeHtml(HttpResponseData response) =>
        ChallengeDetector.LooksLikeHtml(response.ContentType, response.Body);

    private static UsageResult HtmlInsteadOfData(string label, DateTimeOffset now) =>
        UsageResult.Failure(
            RefreshErrorKind.Unauthorized, label,
            "Got a web page instead of usage data — the session has likely expired; re-paste the request.",
            now);
}
