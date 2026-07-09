using UsageWidget.Core.Accounts;
using UsageWidget.Core.Polling;
using UsageWidget.Core.Templating;

namespace UsageWidget.Core.Adapters;

/// <summary>
/// Builds the right adapter per source type. Today every replay-based source uses the generic
/// <see cref="TemplateAdapter"/>; the reserved LocalhostBrowserExtension source will get its own
/// adapter when implemented (it's wired through the enum already).
/// </summary>
public sealed class AdapterFactory
{
    private readonly MappingEngine _mapping = new();
    private readonly PollingSettings _settings;

    public AdapterFactory(PollingSettings settings) => _settings = settings;

    public IProviderAdapter For(AccountSource source) => source switch
    {
        AccountSource.LocalhostBrowserExtension =>
            throw new NotSupportedException("LocalhostBrowserExtension is reserved and not yet implemented."),
        _ => new TemplateAdapter(source, _mapping, _settings.RequestTimeout),
    };
}
