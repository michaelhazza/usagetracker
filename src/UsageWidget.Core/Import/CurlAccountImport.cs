using UsageWidget.Core.Accounts;
using UsageWidget.Core.Templating;

namespace UsageWidget.Core.Import;

/// <summary>
/// Turns a pasted "Copy as cURL" into a ready-to-use <see cref="RequestTemplate"/> + the secret to
/// store. This is the heart of the one-paste flow: the user copies the usage request from DevTools,
/// pastes it, and everything (URL, headers, token, and the number mappings) is filled in for them.
/// </summary>
public static class CurlAccountImport
{
    // Headers whose value is a credential. The first one present becomes the stored secret; any
    // others are dropped so no real secret is ever written to the plaintext template.
    private static readonly string[] SecretHeaders = { "cookie", "authorization", "x-api-key", "anthropic-api-key" };

    // Headers that break a server-side replay or leak nothing useful — stripped on import.
    private static readonly HashSet<string> StripHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept-encoding", "host", "content-length", "connection", "te", "upgrade-insecure-requests",
    };

    public sealed record Imported(RequestTemplate Template, string Secret);

    public static Imported Build(string curl, AccountSource source)
    {
        var parsed = CurlParser.Parse(curl);

        if (!Uri.TryCreate(parsed.Url, UriKind.Absolute, out var uri))
        {
            throw new FormatException("The cURL didn't contain a valid web address.");
        }

        // Collect EVERY credential header, in the stable cookie-first priority of SecretHeaders.
        // Captures routinely carry both a Cookie and an Authorization header, and the replay can
        // need both (e.g. Codex behind Cloudflare: bearer for the API, cookies for the edge) —
        // silently dropping one produced accounts that authenticated on day one and died later.
        var credentials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SecretHeaders)
        {
            if (parsed.Headers.TryGetValue(name, out var value)) credentials[name] = value;
        }

        if (credentials.Count == 0)
        {
            throw new FormatException(
                "Couldn't find your login in the cURL (no cookie or authorization header). " +
                "Re-copy the request that shows your usage numbers.");
        }

        var secret = credentials.Count == 1
            ? credentials.Values.Single()
            : MultiSecret.Serialize(credentials);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in parsed.Headers)
        {
            if (StripHeaders.Contains(name) || name.StartsWith(':')) continue; // pseudo/problem headers

            headers[name] = credentials.ContainsKey(name)
                ? RequestTemplate.TokenPlaceholder // secret injected at send time (contract #3)
                : value;
        }

        var template = new RequestTemplate
        {
            Url = parsed.Url,
            Method = parsed.Method,
            Headers = headers,
            Body = parsed.Body,
            AllowedHosts = new List<string> { uri.Host },
            FollowRedirects = false,
            Mappings = MappingsFor(source),
        };

        return new Imported(template, secret);
    }

    /// <summary>
    /// Pre-baked field locations so the user never sees a JSONPath box. Claude's usage endpoint
    /// returns five_hour / seven_day objects; Codex returns rate_limits.primary/secondary with
    /// duration-based resets (both verified against captured responses — see tests/fixtures).
    /// An imported account must never ship an EMPTY mapping: its per-account template shadows the
    /// shared one, which would leave the row a permanent "config problem".
    /// </summary>
    private static MappingConfig MappingsFor(AccountSource source) => source switch
    {
        AccountSource.ClaudeWebToken => new MappingConfig
        {
            SessionPct = "$.five_hour.utilization",
            SessionReset = "$.five_hour.resets_at",
            SessionResetKind = ResetKind.Timestamp,
            WeeklyPct = "$.seven_day.utilization",
            WeeklyReset = "$.seven_day.resets_at",
            WeeklyResetKind = ResetKind.Timestamp,
        },
        AccountSource.CodexPastedToken or AccountSource.CodexAuthJson => new MappingConfig
        {
            SessionPct = "$.rate_limits.primary.used_percent",
            SessionReset = "$.rate_limits.primary.reset_after_seconds",
            SessionResetKind = ResetKind.DurationSeconds,
            WeeklyPct = "$.rate_limits.secondary.used_percent",
            WeeklyReset = "$.rate_limits.secondary.reset_after_seconds",
            WeeklyResetKind = ResetKind.DurationSeconds,
        },
        _ => new MappingConfig(),
    };
}
