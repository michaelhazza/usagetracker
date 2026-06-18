namespace UsageWidget.Core.Accounts;

/// <summary>
/// Non-secret account metadata. The secret itself never lives here — it is stored in the
/// platform secret store keyed by <see cref="Id"/> + <see cref="Source"/> (contract #3).
/// </summary>
public sealed class Account
{
    /// <summary>
    /// Stable internal identifier. Credential Manager entries are keyed by this, NOT the nickname,
    /// so renaming never orphans or duplicates a secret.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public AccountSource Source { get; init; }

    /// <summary>User-chosen label; falls back to resolved identity if the response provides one.</summary>
    public string? Nickname { get; set; }

    /// <summary>Display order in the popup.</summary>
    public int Order { get; set; }

    /// <summary>
    /// Optional path used only by <see cref="AccountSource.CodexAuthJson"/> sources.
    /// </summary>
    public string? AuthJsonPath { get; set; }

    /// <summary>
    /// Per-account request template. Set when the account is imported from a captured cURL (each
    /// Claude account has its own usage URL/org), and used in preference to the shared per-source
    /// template. Null falls back to the shared template for the source.
    /// </summary>
    public Templating.RequestTemplate? Template { get; set; }

    public string DisplayLabel(string? resolvedIdentity) =>
        resolvedIdentity ?? Nickname ?? $"{Source} {Id[..Math.Min(6, Id.Length)]}";
}
