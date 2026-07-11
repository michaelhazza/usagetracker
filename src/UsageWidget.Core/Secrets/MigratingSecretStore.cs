using System.Collections.Concurrent;
using UsageWidget.Core.Accounts;

namespace UsageWidget.Core.Secrets;

/// <summary>
/// Reads from the primary store and falls back to a legacy store, migrating hits forward.
/// Exists because the app moved from Credential Manager to the DPAPI file store: without this,
/// every account added before the switch silently reads as "no stored token — re-paste required"
/// after an upgrade, which looks exactly like all accounts losing their connection at once.
/// </summary>
public sealed class MigratingSecretStore : ISecretStore
{
    private readonly ISecretStore _primary;
    private readonly ISecretStore _legacy;
    private readonly ConcurrentDictionary<string, object> _locks = new();

    public MigratingSecretStore(ISecretStore primary, ISecretStore legacy)
    {
        _primary = primary;
        _legacy = legacy;
    }

    public string KeyFor(string accountId, AccountSource source) => _primary.KeyFor(accountId, source);

    /// <summary>New and re-pasted secrets go to the primary store only.</summary>
    public void Set(string accountId, AccountSource source, string secret)
    {
        // Serialize with a concurrent migration write for the same account so a background poll's
        // legacy-migration can never land AFTER (and clobber) a fresh re-paste.
        lock (LockFor(accountId, source))
        {
            _primary.Set(accountId, source, secret);

            // The superseded legacy copy must not remain an active fallback: if the primary blob
            // were later lost or corrupted, Get() would silently resurrect the expired/revoked
            // credential the user just replaced. Best-effort — the re-paste already succeeded.
            try { _legacy.Delete(accountId, source); }
            catch { /* primary write succeeded; cleanup failure must not fail the re-paste */ }
        }
    }

    public string? Get(string accountId, AccountSource source)
    {
        var secret = _primary.Get(accountId, source);
        if (!string.IsNullOrEmpty(secret)) return secret;

        string? legacySecret;
        try
        {
            legacySecret = _legacy.Get(accountId, source);
        }
        catch
        {
            return null; // legacy store unavailable — treat as a miss, never as a crash
        }

        if (string.IsNullOrEmpty(legacySecret)) return null;

        // Migrate forward best-effort. Under the per-account lock, re-check the primary is STILL
        // empty: if a user re-pasted between our miss and here, keep their fresh secret and just
        // return the legacy value for this one call. This read must succeed even if the write doesn't.
        lock (LockFor(accountId, source))
        {
            var current = _primary.Get(accountId, source);
            if (!string.IsNullOrEmpty(current)) return current;

            try
            {
                _primary.Set(accountId, source, legacySecret);
            }
            catch
            {
                return legacySecret; // migration failed — KEEP the legacy copy as the only source
            }

            // Migration landed in the primary store; retire the legacy copy so it can never later
            // resurrect a stale credential. Best-effort — the migrated value is already safe.
            try { _legacy.Delete(accountId, source); }
            catch { /* cleanup failure must not fail this read */ }
        }

        return legacySecret;
    }

    private object LockFor(string accountId, AccountSource source) =>
        _locks.GetOrAdd(KeyFor(accountId, source), _ => new object());

    /// <summary>Account deletion must wipe the credential wherever it lives (contract: cleanup).</summary>
    public bool Delete(string accountId, AccountSource source)
    {
        var deletedPrimary = false;
        var deletedLegacy = false;
        try { deletedPrimary = _primary.Delete(accountId, source); } catch { }
        try { deletedLegacy = _legacy.Delete(accountId, source); } catch { }
        return deletedPrimary || deletedLegacy;
    }
}
