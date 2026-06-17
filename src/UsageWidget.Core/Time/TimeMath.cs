using System.Globalization;

namespace UsageWidget.Core.Time;

/// <summary>
/// Contract #6 time handling. All reset values are parsed/normalized to UTC
/// <see cref="DateTimeOffset"/>; durations are converted to absolute reset times at refresh;
/// countdowns are rendered using local Windows time by the caller.
/// </summary>
public static class TimeMath
{
    /// <summary>
    /// Parse an absolute reset timestamp as UTC. Accepts ISO-8601 (with or without offset) and
    /// Unix epoch seconds/milliseconds expressed as a number. Returns null if unparseable.
    /// </summary>
    public static DateTimeOffset? ParseResetTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        // Numeric epoch (seconds or milliseconds).
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            // Heuristic: ms timestamps are ~13 digits, seconds ~10.
            return s.Length >= 13
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        if (DateTimeOffset.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            return dto.ToUniversalTime();
        }

        return null;
    }

    /// <summary>Convert a duration (seconds) returned by the API into an absolute UTC reset time.</summary>
    public static DateTimeOffset ResetFromDurationSeconds(double seconds, DateTimeOffset now)
        => now.ToUniversalTime().AddSeconds(seconds);

    /// <summary>
    /// Render a human countdown ("4h 32m", "12m 03s", "now"). Caller passes local-time values per
    /// contract #6; the math is offset-agnostic.
    /// </summary>
    public static string FormatCountdown(DateTimeOffset resetAt, DateTimeOffset now)
    {
        var remaining = resetAt - now;
        if (remaining <= TimeSpan.Zero) return "now";

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}h {remaining.Minutes:00}m";
        }

        if (remaining.TotalMinutes >= 1)
        {
            return $"{remaining.Minutes}m {remaining.Seconds:00}s";
        }

        return $"{remaining.Seconds}s";
    }
}
