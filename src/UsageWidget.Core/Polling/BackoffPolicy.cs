using UsageWidget.Core.Model;

namespace UsageWidget.Core.Polling;

/// <summary>
/// §11 exponential backoff for 429 / <see cref="Model.RefreshErrorKind.RateLimited"/> (and
/// Challenge responses, which only escalate when hammered):
/// 5 → 10 → 20 → 40 → 60 min (capped), reset on first success. Per-account state, so one
/// rate-limited account never changes another's cadence (contract #5).
/// </summary>
public sealed class BackoffPolicy
{
    private static readonly TimeSpan[] Steps =
    {
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(20),
        TimeSpan.FromMinutes(40),
        TimeSpan.FromMinutes(60),
    };

    private int _consecutiveRateLimits;

    /// <summary>UTC time before which this account should not be auto-refreshed. Null when clear.</summary>
    public DateTimeOffset? BackoffUntil { get; private set; }

    /// <summary>
    /// What put the account into backoff (RateLimited or Challenge), so skipped cycles can keep
    /// reporting the REAL reason instead of everything degrading to "rate limited".
    /// </summary>
    public RefreshErrorKind Cause { get; private set; } = RefreshErrorKind.RateLimited;

    public bool IsInBackoff(DateTimeOffset now) => BackoffUntil is { } until && now < until;

    /// <summary>Record a 429/challenge and extend the backoff window.</summary>
    public TimeSpan OnRateLimited(DateTimeOffset now, RefreshErrorKind cause = RefreshErrorKind.RateLimited)
    {
        var index = Math.Min(_consecutiveRateLimits, Steps.Length - 1);
        var delay = Steps[index];
        _consecutiveRateLimits++;
        BackoffUntil = now + delay;
        Cause = cause;
        return delay;
    }

    /// <summary>Clear backoff after a successful refresh.</summary>
    public void OnSuccess()
    {
        _consecutiveRateLimits = 0;
        BackoffUntil = null;
        Cause = RefreshErrorKind.RateLimited;
    }
}
