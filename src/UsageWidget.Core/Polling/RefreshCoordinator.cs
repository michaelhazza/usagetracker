using UsageWidget.Core.Accounts;
using UsageWidget.Core.Model;

namespace UsageWidget.Core.Polling;

/// <summary>
/// Runs per-account refreshes with strict isolation (contract #5): every account is fetched
/// independently and a failed, throwing, or slow account never blocks or delays the others.
/// Requests are STAGGERED (§11): account i starts i × stagger after the cycle begins, so four
/// accounts never hit the provider in the same instant — simultaneous bursts from one IP are
/// exactly the fingerprint that draws rate limits and challenges. Also enforces the v3.2
/// manual-refresh rule: a "Refresh now" on an account in 429 backoff requires explicit confirmation.
/// </summary>
public sealed class RefreshCoordinator
{
    /// <summary>The delegate that actually fetches one account (typically an adapter call).</summary>
    public delegate Task<UsageResult> FetchOne(Account account, CancellationToken ct);

    /// <summary>Injectable for tests; production uses <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } =
        (wait, ct) => Task.Delay(wait, ct);

    /// <summary>
    /// Refresh all accounts concurrently. Each is wrapped so a thrown exception becomes an isolated
    /// failure result rather than tearing down the batch.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, UsageResult>> RefreshAllAsync(
        IReadOnlyCollection<Account> accounts,
        FetchOne fetch,
        DateTimeOffset now,
        CancellationToken ct = default,
        TimeSpan stagger = default)
    {
        var tasks = accounts.Select(async (account, index) =>
        {
            if (stagger > TimeSpan.Zero && index > 0)
            {
                await Delay(stagger * index, ct).ConfigureAwait(false);
            }

            var result = await SafeFetchAsync(account, fetch, now, ct).ConfigureAwait(false);
            return (account.Id, result);
        });

        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
        return completed.ToDictionary(x => x.Id, x => x.result);
    }

    private static async Task<UsageResult> SafeFetchAsync(
        Account account, FetchOne fetch, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            return await fetch(account, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Last-resort isolation: an adapter should never throw, but if it does, contain it.
            return UsageResult.Failure(
                RefreshErrorKind.NetworkTimeout,
                account.DisplayLabel(null),
                "Unexpected error during refresh.",
                now);
        }
    }

    /// <summary>
    /// v3.2: manual "Refresh now" bypasses the cadence timer but not safety controls. If the account
    /// is currently in 429 backoff, it may only be retried after explicit user confirmation.
    /// </summary>
    public static bool MayManuallyRefresh(BackoffPolicy backoff, DateTimeOffset now, bool userConfirmed)
        => !backoff.IsInBackoff(now) || userConfirmed;
}
