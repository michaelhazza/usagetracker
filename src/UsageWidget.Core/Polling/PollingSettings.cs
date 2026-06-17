namespace UsageWidget.Core.Polling;

/// <summary>
/// §11 polling parameters. Defaults are conservative starting points; the cadence ceiling is
/// governed by the R3 (account-safety) decision and is user-overridable.
/// </summary>
public sealed class PollingSettings
{
    /// <summary>Default per-account cadence. Requests are staggered, not fired simultaneously.</summary>
    public TimeSpan DefaultCadence { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Slower cadence when the session is locked or the user is idle.</summary>
    public TimeSpan IdleCadence { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Idle threshold after which <see cref="IdleCadence"/> applies.</summary>
    public TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Per-request timeout (contract #5).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Show "stale" once the last good refresh is older than this multiple of the cadence.</summary>
    public double StaleCadenceMultiplier { get; set; } = 2.0;

    public bool IsStale(DateTimeOffset lastGoodRefresh, DateTimeOffset now, TimeSpan cadence) =>
        now - lastGoodRefresh > cadence * StaleCadenceMultiplier;
}
