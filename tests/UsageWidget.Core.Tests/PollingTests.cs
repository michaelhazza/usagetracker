using UsageWidget.Core.Accounts;
using UsageWidget.Core.Model;
using UsageWidget.Core.Polling;
using Xunit;

namespace UsageWidget.Core.Tests;

public class BackoffPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Backoff_grows_exponentially_then_caps()
    {
        var policy = new BackoffPolicy();
        Assert.Equal(TimeSpan.FromMinutes(5), policy.OnRateLimited(Now));
        Assert.Equal(TimeSpan.FromMinutes(10), policy.OnRateLimited(Now));
        Assert.Equal(TimeSpan.FromMinutes(20), policy.OnRateLimited(Now));
        Assert.Equal(TimeSpan.FromMinutes(40), policy.OnRateLimited(Now));
        Assert.Equal(TimeSpan.FromMinutes(60), policy.OnRateLimited(Now));
        Assert.Equal(TimeSpan.FromMinutes(60), policy.OnRateLimited(Now)); // capped
    }

    [Fact]
    public void Success_resets_backoff()
    {
        var policy = new BackoffPolicy();
        policy.OnRateLimited(Now);
        Assert.True(policy.IsInBackoff(Now));
        policy.OnSuccess();
        Assert.False(policy.IsInBackoff(Now));
    }
}

public class RefreshCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task One_slow_or_failing_account_does_not_block_others()
    {
        var fast = new Account { Nickname = "fast", Source = AccountSource.ClaudeWebToken };
        var slow = new Account { Nickname = "slow", Source = AccountSource.ClaudeWebToken };
        var thrower = new Account { Nickname = "boom", Source = AccountSource.ClaudeWebToken };

        var coordinator = new RefreshCoordinator();
        var results = await coordinator.RefreshAllAsync(
            new[] { fast, slow, thrower },
            async (account, ct) =>
            {
                if (account.Id == thrower.Id) throw new InvalidOperationException("kaboom");
                if (account.Id == slow.Id) await Task.Delay(50, ct);
                return UsageResult.Success(account.Nickname!, UsageWindow.Create(null, null, 10, null), null, Now);
            },
            Now);

        Assert.Equal(3, results.Count);
        Assert.True(results[fast.Id].IsSuccess);
        Assert.True(results[slow.Id].IsSuccess);
        Assert.False(results[thrower.Id].IsSuccess); // contained, not propagated
    }

    [Fact]
    public void Manual_refresh_blocked_during_backoff_unless_confirmed()
    {
        var policy = new BackoffPolicy();
        policy.OnRateLimited(Now);

        Assert.False(RefreshCoordinator.MayManuallyRefresh(policy, Now, userConfirmed: false));
        Assert.True(RefreshCoordinator.MayManuallyRefresh(policy, Now, userConfirmed: true));
    }

    [Fact]
    public void Manual_refresh_allowed_when_not_in_backoff()
    {
        Assert.True(RefreshCoordinator.MayManuallyRefresh(new BackoffPolicy(), Now, userConfirmed: false));
    }
}

public class TrayStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    private static UsageResult Ok(double pct) =>
        UsageResult.Success("a", UsageWindow.Create(null, null, pct, null), null, Now);

    [Theory]
    [InlineData(10, UsageSeverity.Green)]
    [InlineData(74.9, UsageSeverity.Green)]
    [InlineData(75, UsageSeverity.Orange)]
    [InlineData(89.9, UsageSeverity.Orange)]
    [InlineData(90, UsageSeverity.Red)]
    [InlineData(100, UsageSeverity.Red)]
    public void Severity_thresholds(double pct, UsageSeverity expected)
    {
        Assert.Equal(expected, TrayStatus.SeverityFor(pct));
    }

    [Fact]
    public void Tray_reflects_worst_session_pct_ignoring_failures()
    {
        var failed = UsageResult.Failure(RefreshErrorKind.Unauthorized, "x", "exp", Now);
        var results = new[] { Ok(20), Ok(91), failed, Ok(50) };

        Assert.Equal(91, TrayStatus.WorstSessionPct(results));
        Assert.Equal(UsageSeverity.Red, TrayStatus.OverallSeverity(results));
    }

    [Fact]
    public void No_session_data_yields_null_severity()
    {
        Assert.Null(TrayStatus.OverallSeverity(new[]
        {
            UsageResult.Failure(RefreshErrorKind.NetworkTimeout, "x", null, Now),
        }));
    }
}

public class PollingSettingsTests
{
    [Fact]
    public void Stale_when_older_than_two_cadences()
    {
        var s = new PollingSettings();
        var now = new DateTimeOffset(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);
        var cadence = TimeSpan.FromMinutes(3);

        Assert.False(s.IsStale(now.AddMinutes(-5), now, cadence));
        Assert.True(s.IsStale(now.AddMinutes(-7), now, cadence));
    }
}
