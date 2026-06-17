using UsageWidget.Core.Accounts;
using UsageWidget.Core.Model;
using UsageWidget.Core.Net;
using UsageWidget.Core.Templating;

namespace UsageWidget.Core.Adapters;

/// <summary>
/// The swappable data-layer seam. An adapter takes a stored credential + a request template and
/// returns the normalized <see cref="UsageResult"/>. Implementations must never throw for an
/// expected failure (expiry, rate-limit, network, challenge, parse) — they return a failure
/// <see cref="UsageResult"/> so per-account isolation (contract #5) holds.
/// </summary>
public interface IProviderAdapter
{
    AccountSource Source { get; }

    Task<UsageResult> FetchAsync(
        Account account,
        RequestTemplate template,
        string secret,
        IHttpSender sender,
        DateTimeOffset now,
        CancellationToken ct);
}
