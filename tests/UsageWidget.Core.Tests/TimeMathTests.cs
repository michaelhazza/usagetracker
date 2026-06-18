using UsageWidget.Core.Model;
using UsageWidget.Core.Time;
using Xunit;

namespace UsageWidget.Core.Tests;

public class TimeMathTests
{
    [Fact]
    public void Parses_iso8601_as_utc()
    {
        var parsed = TimeMath.ParseResetTimestamp("2026-06-17T20:00:00Z");
        Assert.Equal(new DateTimeOffset(2026, 6, 17, 20, 0, 0, TimeSpan.Zero), parsed);
    }

    [Fact]
    public void Parses_offset_timestamp_and_normalizes_to_utc()
    {
        var parsed = TimeMath.ParseResetTimestamp("2026-06-17T15:00:00-05:00");
        Assert.Equal(new DateTimeOffset(2026, 6, 17, 20, 0, 0, TimeSpan.Zero), parsed!.Value.ToUniversalTime());
    }

    [Fact]
    public void Parses_unix_epoch_seconds()
    {
        var parsed = TimeMath.ParseResetTimestamp("1781812800");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1781812800), parsed);
    }

    [Fact]
    public void Converts_duration_to_absolute_reset()
    {
        var now = new DateTimeOffset(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.AddSeconds(3600), TimeMath.ResetFromDurationSeconds(3600, now));
    }

    [Theory]
    [InlineData(0, 0, 0, "now")]
    [InlineData(4, 32, 10, "4h 32m")]
    [InlineData(0, 12, 3, "12m 03s")]
    [InlineData(0, 0, 9, "9s")]
    public void Formats_countdown(int h, int m, int s, string expected)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var resetAt = now.AddHours(h).AddMinutes(m).AddSeconds(s);
        Assert.Equal(expected, TimeMath.FormatCountdown(resetAt, now));
    }

    [Fact]
    public void Reset_label_shows_minutes_within_the_hour()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("Resets in 38 min", TimeMath.FormatResetLabel(now.AddMinutes(38), now));
    }

    [Fact]
    public void Reset_label_shows_hours_and_minutes_within_a_day()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("Resets in 4h 32m", TimeMath.FormatResetLabel(now.AddHours(4).AddMinutes(32), now));
    }

    [Fact]
    public void Reset_label_clamps_sub_minute_to_one_minute()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("Resets in 1 min", TimeMath.FormatResetLabel(now.AddSeconds(20), now));
    }

    [Fact]
    public void Reset_label_shows_resetting_now_when_already_expired()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("Resetting now", TimeMath.FormatResetLabel(now.AddMinutes(-5), now));
    }

    [Fact]
    public void Reset_label_remaining_is_offset_aware()
    {
        // now is 10:00 at +05:00 (= 05:00 UTC); reset is 05:30 UTC => 30 minutes out regardless of
        // the differing offsets. Locks the offset-aware subtraction (the absolute render is machine-
        // local-tz dependent, so only the relative form is asserted cross-machine).
        var now = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(5));
        var resetUtc = new DateTimeOffset(2026, 1, 1, 5, 30, 0, TimeSpan.Zero);
        Assert.Equal("Resets in 30 min", TimeMath.FormatResetLabel(resetUtc, now));
    }

    [Fact]
    public void Reset_label_uses_absolute_form_beyond_a_day()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var label = TimeMath.FormatResetLabel(now.AddDays(3), now);

        // Absolute weekday/time form (e.g. "Resets Sat 12:00 AM"), tz-dependent on render so we only
        // assert it's the absolute branch, not a relative "Resets in …" countdown.
        Assert.StartsWith("Resets ", label);
        Assert.DoesNotContain("in", label);
    }

    [Fact]
    public void Elapsed_fraction_uses_period_start_derived_from_reset()
    {
        var now = new DateTimeOffset(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);
        // 5h window resetting in 1h => 4h elapsed of 5h => 0.8
        var window = UsageWindow.Create(null, null, 50, now.AddHours(1));
        var fraction = window.ElapsedFraction(TimeSpan.FromHours(5), now);
        Assert.Equal(0.8, fraction!.Value, 3);
    }
}
