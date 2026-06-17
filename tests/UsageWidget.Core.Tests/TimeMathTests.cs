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
    public void Elapsed_fraction_uses_period_start_derived_from_reset()
    {
        var now = new DateTimeOffset(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);
        // 5h window resetting in 1h => 4h elapsed of 5h => 0.8
        var window = UsageWindow.Create(null, null, 50, now.AddHours(1));
        var fraction = window.ElapsedFraction(TimeSpan.FromHours(5), now);
        Assert.Equal(0.8, fraction!.Value, 3);
    }
}
