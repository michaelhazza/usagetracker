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

        // Find the credential header to store as the secret (cookie preferred for claude.ai).
        var secretHeader = parsed.Headers.Keys
            .FirstOrDefault(k => SecretHeaders.Contains(k, StringComparer.OrdinalIgnoreCase));
        if (secretHeader is null)
        {
            throw new FormatException(
                "Couldn't find your login in the cURL (no cookie or authorization header). " +
                "Re-copy the request that shows your usage numbers.");
        }

        var secret = parsed.Headers[secretHeader];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in parsed.Headers)
        {
            if (StripHeaders.Contains(name) || name.StartsWith(':')) continue; // pseudo/problem headers

            if (name.Equals(secretHeader, StringComparison.OrdinalIgnoreCase))
            {
                headers[name] = RequestTemplate.TokenPlaceholder; // secret injected at send time
            }
            else if (SecretHeaders.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue; // a second credential header — never persist it in plaintext
            }
            else
            {
                headers[name] = value;
            }
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
    /// returns five_hour / seven_day objects; Codex's backend-api/wham/usage returns a rate_limit
    /// object with primary_window / secondary_window (both verified against live responses).
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
        AccountSource.CodexPastedToken => new MappingConfig
        {
            SessionPct = "$.rate_limit.primary_window.used_percent",
            SessionReset = "$.rate_limit.primary_window.reset_after_seconds",
            SessionResetKind = ResetKind.DurationSeconds,
            WeeklyPct = "$.rate_limit.secondary_window.used_percent",
            WeeklyReset = "$.rate_limit.secondary_window.reset_after_seconds",
            WeeklyResetKind = ResetKind.DurationSeconds,
        },
        _ => new MappingConfig(),
    };
}
