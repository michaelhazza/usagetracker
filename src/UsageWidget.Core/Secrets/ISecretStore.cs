using UsageWidget.Core.Accounts;

namespace UsageWidget.Core.Secrets;

/// <summary>
/// Contract #3: secrets live only here (Windows Credential Manager in production). Entries are keyed
/// by the stable internal account ID + source type — never the nickname — so renaming an account
/// never orphans or duplicates a credential (v3.1).
/// </summary>
public interface ISecretStore
{
    /// <summary>Stable key form: <c>UsageWidget/{accountId}/{sourceType}</c>.</summary>
    string KeyFor(string accountId, AccountSource source);

    void Set(string accountId, AccountSource source, string secret);

    string? Get(string accountId, AccountSource source);

    /// <summary>Removes the secret. Called when an account is deleted (graceful cleanup).</summary>
    bool Delete(string accountId, AccountSource source);
}

/// <summary>Shared key derivation so every implementation agrees on the stable naming scheme.</summary>
public static class SecretKey
{
    public const string Prefix = "UsageWidget";

    public static string For(string accountId, AccountSource source) => $"{Prefix}/{accountId}/{source}";
}
