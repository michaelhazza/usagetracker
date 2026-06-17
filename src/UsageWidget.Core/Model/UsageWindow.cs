namespace UsageWidget.Core.Model;

/// <summary>
/// A single usage window (session = rolling 5h, weekly = 7d). Per §3 the mapping accepts EITHER
/// raw counts (<see cref="Used"/>/<see cref="Limit"/>) OR a percentage (<see cref="Pct"/>):
/// when counts are present, <see cref="Pct"/> is derived; when only a percentage is present, the
/// counts stay null — absolute numbers are never discarded when the endpoint provides them.
/// </summary>
public sealed record UsageWindow
{
    public double? Pct { get; init; }
    public long? Used { get; init; }
    public long? Limit { get; init; }
    public DateTimeOffset? ResetAt { get; init; }

    /// <summary>True if this window resolved enough data to display (per §3 validation rule).</summary>
    public bool HasData => Pct is not null || (Used is not null && Limit is not null);

    /// <summary>
    /// Build a window applying the counts-or-percent normalization rule.
    /// </summary>
    public static UsageWindow Create(long? used, long? limit, double? pct, DateTimeOffset? resetAt)
    {
        double? resolvedPct = pct;
        if (resolvedPct is null && used is not null && limit is > 0)
        {
            resolvedPct = Math.Round((double)used.Value / limit.Value * 100.0, 2);
        }

        return new UsageWindow
        {
            Used = used,
            Limit = limit,
            Pct = resolvedPct,
            ResetAt = resetAt,
        };
    }

    /// <summary>
    /// Fraction (0..1) of the period that has elapsed, for the optional "elapsed time" marker.
    /// Period start is derived as <paramref name="resetAt"/> − <paramref name="periodLength"/>
    /// (contract #6). Returns null if there is no reset time.
    /// </summary>
    public double? ElapsedFraction(TimeSpan periodLength, DateTimeOffset now)
    {
        if (ResetAt is null) return null;
        var start = ResetAt.Value - periodLength;
        var total = (ResetAt.Value - start).TotalSeconds;
        if (total <= 0) return null;
        var elapsed = (now - start).TotalSeconds;
        return Math.Clamp(elapsed / total, 0.0, 1.0);
    }
}
