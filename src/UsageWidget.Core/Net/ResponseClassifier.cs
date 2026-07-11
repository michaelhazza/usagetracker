using UsageWidget.Core.Model;
using UsageWidget.Core.Security;

namespace UsageWidget.Core.Net;

/// <summary>A non-OK classification: the taxonomy kind plus a user-safe, actionable message.</summary>
public sealed record Classification(RefreshErrorKind Kind, string Message);

/// <summary>
/// Maps a raw response to either "OK, proceed to mapping" (null) or a <see cref="Classification"/>
/// (§3). Challenge detection runs FIRST: a Cloudflare interstitial often arrives as 403/503 with
/// HTML, and must classify as <see cref="RefreshErrorKind.Challenge"/> rather than
/// Unauthorized/ParseFailed. Distinct statuses get distinct messages — a proxy that wants sign-in
/// (407) and an expired session (401) need completely different user action.
/// </summary>
public static class ResponseClassifier
{
    public static Classification? Classify(HttpResponseData response)
    {
        if (ChallengeDetector.IsChallenge(response.ContentType, response.Body))
        {
            return new Classification(
                RefreshErrorKind.Challenge,
                "Blocked by a security check (e.g. Cloudflare) — backing off before retrying.");
        }

        return response.StatusCode switch
        {
            >= 200 and < 300 => null, // OK — proceed to mapping

            // A 403 that comes back as a web PAGE is the edge/WAF blocking this client, not the
            // provider rejecting the token — telling the user to re-paste would send them in
            // circles. Back off like a challenge instead. (Marker-bearing pages were already
            // caught by ChallengeDetector above; this catches marker-less block pages.)
            403 when ChallengeDetector.LooksLikeHtml(response.ContentType, response.Body) =>
                new Classification(
                    RefreshErrorKind.Challenge,
                    "Blocked at the network edge (HTTP 403) — backing off before retrying."),

            401 or 403 => new Classification(
                RefreshErrorKind.Unauthorized,
                "Token expired or unauthorized — re-paste required."),

            // Redirects are off by default (contract #4); providers answer a dead web session
            // with a redirect to their login page, so treat it as the session expiring.
            >= 300 and < 400 => new Classification(
                RefreshErrorKind.Unauthorized,
                "The server redirected the request — the session has likely expired; re-paste if this persists."),

            // The PROXY wants credentials, not the provider — a completely different fix
            // than re-pasting a token. Transient from the app's point of view: retried next cycle.
            407 => new Classification(
                RefreshErrorKind.NetworkTimeout,
                "Your network proxy requires sign-in (HTTP 407) — check the system proxy settings."),

            408 => new Classification(
                RefreshErrorKind.NetworkTimeout,
                "The server timed out (HTTP 408) — will retry."),

            429 => new Classification(
                RefreshErrorKind.RateLimited,
                "Rate limited by the provider — backing off."),

            502 or 503 or 504 => new Classification(
                RefreshErrorKind.NetworkTimeout,
                $"Server or gateway problem (HTTP {response.StatusCode}) — will retry."),

            >= 500 => new Classification(
                RefreshErrorKind.NetworkTimeout,
                $"Server error (HTTP {response.StatusCode}) — will retry."),

            _ => new Classification(
                RefreshErrorKind.ParseFailed,
                $"Unexpected response (HTTP {response.StatusCode}) — check the request template."),
        };
    }
}
