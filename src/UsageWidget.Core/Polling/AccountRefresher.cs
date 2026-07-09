using System.Collections.Concurrent;
using UsageWidget.Core.Accounts;
using UsageWidget.Core.Adapters;
using UsageWidget.Core.Config;
using UsageWidget.Core.Model;
using UsageWidget.Core.Net;
using UsageWidget.Core.Secrets;

namespace UsageWidget.Core.Polling;

/// <summary>
/// Resolves credential + template per account and runs all refreshes with per-account isolation
/// (contract #5). Holds per-account <see cref="BackoffPolicy"/> state so a rate-limited account never
/// changes another's cadence, and enforces the manual-refresh-during-backoff rule (v3.2).
/// </summary>
public sealed class AccountRefresher
{
    private readonly AdapterFactory _factory;
    private readonly ISecretStore _secrets;
    private readonly IHttpSender _sender;
    private readonly RefreshCoordinator _coordinator = new();

    // Accounts refresh concurrently (contract #5), so per-account backoff state must be
    // concurrency-safe — a plain Dictionary would race on first-touch inserts.
    private readonly ConcurrentDictionary<string, BackoffPolicy> _backoff = new();

    public AccountRefresher(AdapterFactory factory, ISecretStore secrets, IHttpSender sender)
    {
        _factory = factory;
        _secrets = secrets;
        _sender = sender;
    }

    public BackoffPolicy BackoffFor(string accountId) =>
        _backoff.GetOrAdd(accountId, _ => new BackoffPolicy());

    /// <summary>
    /// Forget an account's backoff. Called when the user re-pastes a token or template: they just
    /// fixed the problem, so the next refresh must try immediately instead of telling them to
    /// wait out a backoff window that no longer applies.
    /// </summary>
    public void ResetBackoff(string accountId) => _backoff.TryRemove(accountId, out _);

    public Task<IReadOnlyDictionary<string, UsageResult>> RefreshAllAsync(
        AdapterConfig config, DateTimeOffset now, CancellationToken ct)
    {
        // Snapshot: the UI thread adds/removes accounts on this same config object, and a
        // mid-cycle mutation of the live List would throw and discard the whole batch.
        var accounts = config.Accounts.ToArray();
        PruneRemovedAccounts(accounts);

        return _coordinator.RefreshAllAsync(
            accounts,
            (account, token) => RefreshOneAsync(account, config, now, token),
            now,
            ct,
            config.Polling.StaggerInterval);
    }

    /// <summary>Drop backoff state for accounts that no longer exist (delete + re-add must start clean).</summary>
    private void PruneRemovedAccounts(IReadOnlyCollection<Account> accounts)
    {
        foreach (var id in _backoff.Keys)
        {
            if (!accounts.Any(a => a.Id == id)) _backoff.TryRemove(id, out _);
        }
    }

    private async Task<UsageResult> RefreshOneAsync(
        Account account, AdapterConfig config, DateTimeOffset now, CancellationToken ct)
    {
        var backoff = BackoffFor(account.Id);
        if (backoff.IsInBackoff(now))
        {
            // Keep reporting the REAL cause (challenge vs rate limit) and when the retry comes,
            // instead of a bare "In backoff." that reads like a brand-new failure every cycle.
            var wait = backoff.BackoffUntil!.Value - now;
            var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
            var reason = backoff.Cause == RefreshErrorKind.Challenge
                ? "Security check active"
                : "Rate limited";
            return UsageResult.Failure(
                backoff.Cause, account.DisplayLabel(null),
                $"{reason} — retrying in ~{minutes} min.", now);
        }

        // Prefer the account's own captured template (its org-specific URL); fall back to shared.
        var template = account.Template ?? config.TemplateFor(account.Source);
        if (template is null || template.IsPlaceholder)
        {
            return UsageResult.Failure(
                RefreshErrorKind.ParseFailed, account.DisplayLabel(null),
                "No request template configured — open the Request Template editor.", now);
        }

        var secret = _secrets.Get(account.Id, account.Source);
        if (string.IsNullOrEmpty(secret))
        {
            return UsageResult.Failure(
                RefreshErrorKind.Unauthorized, account.DisplayLabel(null),
                "No stored token — re-paste required.", now);
        }

        var adapter = _factory.For(account.Source);
        var result = await adapter.FetchAsync(account, template, secret, _sender, now, ct).ConfigureAwait(false);

        if (result.IsSuccess) backoff.OnSuccess();
        // Back off on a 429 AND on a Cloudflare/anti-automation challenge: hammering a challenged
        // endpoint at the polling cadence only escalates the block. Transient Stale/timeout is left
        // to retry next cycle so a brief network blip doesn't make the widget look dead.
        else if (result.ErrorKind is RefreshErrorKind.RateLimited or RefreshErrorKind.Challenge)
        {
            backoff.OnRateLimited(now, result.ErrorKind.Value);
        }

        return result;
    }
}
