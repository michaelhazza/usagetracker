using UsageWidget.Core.Model;
using UsageWidget.Core.Security;

namespace UsageWidget.Core.Net;

/// <summary>
/// Maps a raw response to either "OK, proceed to mapping" or a <see cref="RefreshErrorKind"/> (§3).
/// Challenge detection runs FIRST: a Cloudflare interstitial often arrives as 403/503 with HTML, and
/// must classify as <see cref="RefreshErrorKind.Challenge"/> rather than Unauthorized/ParseFailed.
/// </summary>
public static class ResponseClassifier
{
    public static RefreshErrorKind? Classify(HttpResponseData response)
    {
        if (ChallengeDetector.IsChallenge(response.ContentType, response.Body))
        {
            return RefreshErrorKind.Challenge;
        }

        return response.StatusCode switch
        {
            401 or 403 => RefreshErrorKind.Unauthorized,
            429 => RefreshErrorKind.RateLimited,
            >= 200 and < 300 => null, // OK — proceed to mapping
            >= 500 => RefreshErrorKind.NetworkTimeout, // transient server-side
            _ => RefreshErrorKind.ParseFailed, // other 4xx: endpoint/template is wrong
        };
    }
}
