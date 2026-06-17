namespace UsageWidget.Core.Model;

/// <summary>
/// §3 error taxonomy. Each failed refresh resolves to exactly one of these; the UI maps each to a
/// distinct per-row state. This is an enum, never a free-text string.
/// </summary>
public enum RefreshErrorKind
{
    /// <summary>401/403 — expired or invalid credential. Drives the "re-paste token" prompt.</summary>
    Unauthorized,

    /// <summary>429 — rate limited. Drives backoff; shown as "rate limited", not a generic error.</summary>
    RateLimited,

    /// <summary>DNS/connect/read timeout or transport failure. Shown as "stale"; retried next cycle.</summary>
    NetworkTimeout,

    /// <summary>Edge/anti-bot interstitial (e.g. Cloudflare). Needs full-header replay or the extension path.</summary>
    Challenge,

    /// <summary>HTTP 200 but the mappings did not resolve. A configuration problem — point at the editor.</summary>
    ParseFailed,
}
