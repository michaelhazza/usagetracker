namespace UsageWidget.Core.Polling;

/// <summary>
/// §11 polling parameters. Defaults are conservative starting points; the cadence ceiling is
/// governed by the R3 (account-safety) decision and is user-overridable.
/// </summary>
public sealed class PollingSettings
{
    /// <summary>
    /// Default per-account cadence (§11: 3 min). Polling faster than this against edge-protected
    /// providers is exactly the automation fingerprint that draws rate limits and challenges (R3).
    /// </summary>
    public TimeSpan DefaultCadence { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Slower cadence when the session is locked or the user is idle.</summary>
    public TimeSpan IdleCadence { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Idle threshold after which <see cref="IdleCadence"/> applies.</summary>
    public TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Per-request timeout (contract #5).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <see cref="RequestTimeout"/> clamped to a sane range. Consumers use this so a hand-edited
    /// value can't be zero/negative (CancelAfter throws), absurdly large (a request outliving the
    /// transport's rotate-and-dispose window), or otherwise destabilizing.
    /// </summary>
    public TimeSpan EffectiveRequestTimeout => Clamp(RequestTimeout, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(90));

    /// <summary>
    /// §11: requests are staggered, not fired simultaneously — account i starts i × this after the
    /// cycle begins, so multiple accounts never burst at the provider in one instant.
    /// </summary>
    public TimeSpan StaggerInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary><see cref="StaggerInterval"/> clamped so a bad value can't stall or overrun a cycle.</summary>
    public TimeSpan EffectiveStagger => Clamp(StaggerInterval, TimeSpan.Zero, TimeSpan.FromSeconds(30));

    /// <summary>
    /// Total send attempts per refresh for TRANSIENT failures (timeouts, DNS/connect errors,
    /// 408/5xx). 2 = one quick jittered retry; 1 disables in-cycle retries. Clamped by
    /// <see cref="Net.RetryingHttpSender"/> so a large hand-edited value can't freeze the widget.
    /// </summary>
    public int TransientRetryAttempts { get; set; } = 2;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    /// <summary>Show "stale" once the last good refresh is older than this multiple of the cadence.</summary>
    public double StaleCadenceMultiplier { get; set; } = 2.0;

    /// <summary>
    /// The age beyond which a row's last good data counts as stale (§11), computed from the CURRENT
    /// cadence — the host passes the idle or default cadence so idle polls aren't wrongly flagged stale.
    /// </summary>
    public TimeSpan StaleAfterFor(TimeSpan currentCadence) => currentCadence * StaleCadenceMultiplier;

    public bool IsStale(DateTimeOffset lastGoodRefresh, DateTimeOffset now, TimeSpan cadence) =>
        now - lastGoodRefresh > cadence * StaleCadenceMultiplier;
}
