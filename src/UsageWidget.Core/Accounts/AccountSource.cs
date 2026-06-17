namespace UsageWidget.Core.Accounts;

/// <summary>
/// Distinct credential/source classes. Contract #1: these are never interchangeable — a
/// <see cref="ClaudeWebToken"/> must never be used to call api.anthropic.com, etc.
/// </summary>
public enum AccountSource
{
    /// <summary>Primary path: a claude.ai web session token, replaying the Usage page request.</summary>
    ClaudeWebToken,

    /// <summary>Optional, opt-in fallback. Billing-capable; never used for web-token accounts.</summary>
    AnthropicApiKey,

    /// <summary>Codex usage via a pasted token.</summary>
    CodexPastedToken,

    /// <summary>Codex usage by reading %USERPROFILE%\.codex\auth.json (and WSL ~/.codex/auth.json).</summary>
    CodexAuthJson,

    /// <summary>Reserved. MV3 browser extension POSTing usage to a localhost listener. Wired now, implemented later.</summary>
    LocalhostBrowserExtension,
}
