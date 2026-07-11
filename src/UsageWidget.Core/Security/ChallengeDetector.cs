namespace UsageWidget.Core.Security;

/// <summary>
/// §3 / v3.2. Detects edge/anti-bot interstitials (e.g. Cloudflare) so they classify as
/// <see cref="Model.RefreshErrorKind.Challenge"/> rather than <see cref="Model.RefreshErrorKind.ParseFailed"/>.
/// A challenge is a NON-JSON (typically HTML) response from an allowlisted host that contains a
/// recognizable challenge marker.
/// </summary>
public static class ChallengeDetector
{
    // Only markers UNIQUE to challenge/block interstitials. Deliberately absent: the bare word
    // "cloudflare" (it appears on every Cloudflare-served page, including transient 5xx error
    // pages — flagging those as Challenge turns a routine gateway blip into a 5–60 min backoff)
    // and generic "enable JavaScript" noscript text (any SPA login page contains it).
    private static readonly string[] Markers =
    {
        "cf-chl",              // Cloudflare challenge token / element ids
        "_cf_chl_opt",
        "cf-turnstile",        // Cloudflare Turnstile widget
        "challenge-platform",  // /cdn-cgi/challenge-platform/ script on every challenge page
        "just a moment",       // Cloudflare interstitial title
        "checking your browser",
        "attention required",  // Cloudflare WAF block page title
        "captcha",
    };

    /// <summary>
    /// True if the response looks like a challenge interstitial. JSON responses are never
    /// challenges (they go to the mapping engine, which may legitimately fail to map).
    /// </summary>
    public static bool IsChallenge(string? contentType, string? body)
    {
        if (LooksLikeJson(contentType, body)) return false;
        if (string.IsNullOrWhiteSpace(body)) return false;

        var haystack = body.ToLowerInvariant();
        foreach (var marker in Markers)
        {
            if (haystack.Contains(marker, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static bool LooksLikeJson(string? contentType, string? body)
    {
        if (!string.IsNullOrEmpty(contentType) &&
            contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = body?.TrimStart();
        return !string.IsNullOrEmpty(trimmed) && (trimmed[0] == '{' || trimmed[0] == '[');
    }

    /// <summary>
    /// True when the response is a web PAGE rather than data — used to tell "the provider served
    /// its login/block page" apart from "the JSON shape changed" (§3 classification honesty).
    /// </summary>
    public static bool LooksLikeHtml(string? contentType, string? body)
    {
        if (!string.IsNullOrEmpty(contentType) &&
            contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = body?.TrimStart();
        return !string.IsNullOrEmpty(trimmed) && trimmed[0] == '<';
    }
}
