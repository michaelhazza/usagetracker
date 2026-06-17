namespace UsageWidget.Core.Security;

/// <summary>
/// §3 / v3.2. Detects edge/anti-bot interstitials (e.g. Cloudflare) so they classify as
/// <see cref="Model.RefreshErrorKind.Challenge"/> rather than <see cref="Model.RefreshErrorKind.ParseFailed"/>.
/// A challenge is a NON-JSON (typically HTML) response from an allowlisted host that contains a
/// recognizable challenge marker.
/// </summary>
public static class ChallengeDetector
{
    private static readonly string[] Markers =
    {
        "cf-chl",            // Cloudflare challenge token / element ids
        "cloudflare",
        "just a moment",     // Cloudflare interstitial title
        "captcha",
        "attention required",
        "enable javascript", // "Please enable JavaScript" challenge pages
        "javascript is required",
        "checking your browser",
        "_cf_chl_opt",
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
}
