namespace UsageWidget.Core.Model;

public enum UsageSeverity
{
    Green,  // < 75%
    Orange, // 75–90%
    Red,    // >= 90%
}

/// <summary>
/// Derives the tray-icon state from all accounts: the tray reflects the WORST (highest) current
/// session % across accounts, color-coded green &lt; 75, orange 75–90, red ≥ 90.
/// </summary>
public static class TrayStatus
{
    public static UsageSeverity SeverityFor(double pct) => pct switch
    {
        >= 90 => UsageSeverity.Red,
        >= 75 => UsageSeverity.Orange,
        _ => UsageSeverity.Green,
    };

    /// <summary>
    /// Highest current-session % across successful results, or null if none have session data.
    /// Failed accounts are skipped (they show their own per-row state) but never suppress others.
    /// </summary>
    public static double? WorstSessionPct(IEnumerable<UsageResult> results)
    {
        double? worst = null;
        foreach (var r in results)
        {
            if (!r.IsSuccess) continue;
            var pct = r.Session?.Pct;
            if (pct is null) continue;
            worst = worst is null ? pct : Math.Max(worst.Value, pct.Value);
        }

        return worst;
    }

    public static UsageSeverity? OverallSeverity(IEnumerable<UsageResult> results)
    {
        var worst = WorstSessionPct(results);
        return worst is null ? null : SeverityFor(worst.Value);
    }
}
