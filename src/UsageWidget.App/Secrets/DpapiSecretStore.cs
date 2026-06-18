using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using UsageWidget.Core.Accounts;

namespace UsageWidget.Core.Secrets;

/// <summary>
/// Secret store backed by DPAPI (Windows Data Protection API), per-user scope. Each secret is
/// encrypted to the current Windows user and written to a file under %APPDATA%. Used instead of
/// Credential Manager because browser session cookies routinely exceed Credential Manager's
/// ~2.5 KB blob limit. Still satisfies "encrypt secrets, not config": the on-disk blob is
/// unreadable without the logged-in user's credentials, and never touches adapter-config.json.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _dir;

    public DpapiSecretStore(string? directory = null)
    {
        _dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UsageWidget", "secrets");
        Directory.CreateDirectory(_dir);
    }

    public string KeyFor(string accountId, AccountSource source) => SecretKey.For(accountId, source);

    public void Set(string accountId, AccountSource source, string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        var encrypted = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(accountId, source), encrypted);
    }

    public string? Get(string accountId, AccountSource source)
    {
        var path = PathFor(accountId, source);
        if (!File.Exists(path)) return null;

        try
        {
            var encrypted = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null; // corrupted or not decryptable by this user
        }
    }

    public bool Delete(string accountId, AccountSource source)
    {
        var path = PathFor(accountId, source);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private string PathFor(string accountId, AccountSource source) =>
        Path.Combine(_dir, $"{accountId}.{source}.bin");
}
