using UsageWidget.Core.Accounts;
using UsageWidget.Core.Config;
using UsageWidget.Core.Model;
using UsageWidget.Core.Net;
using UsageWidget.Core.Polling;
using UsageWidget.Core.Secrets;

namespace UsageWidget.App.Services;

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
    private readonly Dictionary<string, BackoffPolicy> _backoff = new();

    public AccountRefresher(AdapterFactory factory, ISecretStore secrets, IHttpSender sender)
    {
        _factory = factory;
        _secrets = secrets;
        _sender = sender;
    }

    public BackoffPolicy BackoffFor(string accountId) =>
        _backoff.TryGetValue(accountId, out var b) ? b : _backoff[accountId] = new BackoffPolicy();

    public Task<IReadOnlyDictionary<string, UsageResult>> RefreshAllAsync(
        AdapterConfig config, DateTimeOffset now, CancellationToken ct, bool force = false)
    {
        return _coordinator.RefreshAllAsync(config.Accounts, (account, token) =>
            RefreshOneAsync(account, config, now, token, force), now, ct);
    }

    private async Task<UsageResult> RefreshOneAsync(
        Account account, AdapterConfig config, DateTimeOffset now, CancellationToken ct, bool force = false)
    {
        // Auto-poll skips accounts in backoff; a forced manual refresh (the header ↻) overrides it —
        // the user is explicitly asking to retry now (v3.2 manual-refresh rule).
        var backoff = BackoffFor(account.Id);
        if (!RefreshCoordinator.MayManuallyRefresh(backoff, now, userConfirmed: force))
        {
            return UsageResult.Failure(
                RefreshErrorKind.RateLimited, account.DisplayLabel(null), "In backoff.", now);
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
        // endpoint at the 60s cadence only escalates the block. Transient Stale/timeout is left to
        // retry next cycle so a brief network blip doesn't make the widget look dead.
        else if (result.ErrorKind is RefreshErrorKind.RateLimited or RefreshErrorKind.Challenge)
        {
            backoff.OnRateLimited(now);
        }

        return result;
    }
}
