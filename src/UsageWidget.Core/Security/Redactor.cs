using System.Text.RegularExpressions;

namespace UsageWidget.Core.Security;

/// <summary>
/// Contract #7. Scrubs secrets and identifiers from any string before it reaches a log, diagnostic,
/// error dialog, toast, debug panel, or copied diagnostic (v3.2: every surface, not just files).
/// Fail safe: redaction is applied broadly and may over-redact, never under-redact.
/// </summary>
public static partial class Redactor
{
    public const string Placeholder = "[REDACTED]";

    // Sensitive HTTP header names whose VALUES must never be shown.
    private static readonly string[] SensitiveHeaders =
    {
        "Authorization", "Cookie", "Set-Cookie", "Proxy-Authorization", "X-Api-Key", "Anthropic-Api-Key",
    };

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    // Bearer / sk- / JWT-ish / long opaque tokens.
    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._\-]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9\-_]{8,}")]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+")]
    private static partial Regex JwtRegex();

    // sessionKey=..., access_token=..., refresh_token=..., org id / account id key=value pairs.
    [GeneratedRegex(@"(?i)\b(sessionKey|session_id|sessionid|access_token|refresh_token|accessToken|refreshToken|org[_-]?id|account[_-]?id|orgId|accountId)\b""?\s*[=:]\s*""?[A-Za-z0-9._\-]+""?")]
    private static partial Regex KeyValueSecretRegex();

    // Header-line form: "Name: value" for sensitive header names.
    private static readonly Regex HeaderLineRegex = new(
        @"(?im)^(?<name>" + string.Join("|", SensitiveHeaders.Select(Regex.Escape)) + @")\s*:\s*.*$",
        RegexOptions.Compiled);

    /// <summary>Redact a free-text string (log line, message, diagnostic).</summary>
    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;

        var s = input;
        s = HeaderLineRegex.Replace(s, m => $"{m.Groups["name"].Value}: {Placeholder}");
        s = KeyValueSecretRegex().Replace(s, m => RedactKeyValue(m.Value));
        s = JwtRegex().Replace(s, Placeholder);
        s = BearerRegex().Replace(s, $"Bearer {Placeholder}");
        s = ApiKeyRegex().Replace(s, Placeholder);
        s = EmailRegex().Replace(s, Placeholder);
        return s;
    }

    /// <summary>
    /// Redact a single header value by name. Sensitive headers are fully masked; others are
    /// scrubbed for embedded secrets (a non-sensitive header can still carry a token).
    /// </summary>
    public static string RedactHeaderValue(string headerName, string value)
    {
        if (SensitiveHeaders.Any(h => string.Equals(h, headerName, StringComparison.OrdinalIgnoreCase)))
        {
            return Placeholder;
        }

        return Redact(value);
    }

    /// <summary>
    /// Redact a URL: keeps scheme/host/path, drops the query string entirely (it may carry tokens).
    /// </summary>
    public static string RedactUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return Redact(url);
        var baseUrl = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        return uri.Query.Length > 0 ? $"{baseUrl}?{Placeholder}" : baseUrl;
    }

    private static string RedactKeyValue(string match)
    {
        var sepIndex = match.IndexOfAny(new[] { '=', ':' });
        return sepIndex < 0 ? Placeholder : $"{match[..(sepIndex + 1)]}{Placeholder}";
    }
}
