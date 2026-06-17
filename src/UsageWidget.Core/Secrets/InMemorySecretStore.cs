using System.Collections.Concurrent;
using UsageWidget.Core.Accounts;

namespace UsageWidget.Core.Secrets;

/// <summary>
/// Non-persistent store for tests and non-Windows hosts. Same keying scheme as the real store so
/// behavior (including rename-safety) is identical in tests.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

    public string KeyFor(string accountId, AccountSource source) => SecretKey.For(accountId, source);

    public void Set(string accountId, AccountSource source, string secret) =>
        _store[KeyFor(accountId, source)] = secret;

    public string? Get(string accountId, AccountSource source) =>
        _store.TryGetValue(KeyFor(accountId, source), out var v) ? v : null;

    public bool Delete(string accountId, AccountSource source) =>
        _store.TryRemove(KeyFor(accountId, source), out _);
}
