using UsageWidget.Core.Accounts;
using UsageWidget.Core.Polling;
using UsageWidget.Core.Templating;

namespace UsageWidget.Core.Config;

/// <summary>
/// The plaintext, user-editable adapter config (contract #3), persisted to
/// <c>%APPDATA%\UsageWidget\adapter-config.json</c>. Holds NO secrets — templates carry only the
/// <c>{{TOKEN}}</c> placeholder. Carries a <see cref="SchemaVersion"/> for forward migration (§4).
/// </summary>
public sealed class AdapterConfig
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Default request template per source type (keyed by <see cref="AccountSource"/> name).</summary>
    public Dictionary<string, RequestTemplate> TemplatesBySource { get; set; } = new();

    public List<Account> Accounts { get; set; } = new();

    public PollingSettings Polling { get; set; } = new();

    /// <summary>Last on-screen position of the popup widget, so it reopens where the user left it.</summary>
    public WindowBounds? Window { get; set; }

    /// <summary>When true, the widget stays on top and does not auto-hide on focus loss (pinned).</summary>
    public bool Pinned { get; set; }

    public RequestTemplate? TemplateFor(AccountSource source) =>
        TemplatesBySource.TryGetValue(source.ToString(), out var t) ? t : null;
}
