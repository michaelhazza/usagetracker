using System.Windows;
using System.Windows.Controls;
using UsageWidget.Core.Accounts;
using UsageWidget.Core.Config;
using UsageWidget.Core.Templating;

namespace UsageWidget.App.UI;

public partial class TemplateEditorWindow : Window
{
    private readonly AdapterConfig _config;
    private AccountSource _current;

    private sealed record SourceOption(string Display, AccountSource Source);

    public TemplateEditorWindow(AdapterConfig config)
    {
        _config = config;
        InitializeComponent();

        SessionResetKindCombo.ItemsSource = Enum.GetNames<ResetKind>();
        WeeklyResetKindCombo.ItemsSource = Enum.GetNames<ResetKind>();

        SourceCombo.ItemsSource = new[]
        {
            new SourceOption("Claude (web token)", AccountSource.ClaudeWebToken),
            new SourceOption("Codex (pasted token)", AccountSource.CodexPastedToken),
            new SourceOption("Anthropic API key", AccountSource.AnthropicApiKey),
        };
        SourceCombo.SelectedIndex = 0;
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceCombo.SelectedItem is not SourceOption option) return;
        _current = option.Source;
        LoadIntoFields(_config.TemplateFor(_current) ?? new RequestTemplate());
    }

    private void LoadIntoFields(RequestTemplate t)
    {
        UrlBox.Text = t.Url;
        MethodBox.Text = t.Method;
        HostsBox.Text = string.Join(", ", t.AllowedHosts);
        HeadersBox.Text = string.Join(Environment.NewLine, t.Headers.Select(h => $"{h.Key}: {h.Value}"));

        SessionPctBox.Text = t.Mappings.SessionPct ?? "";
        SessionResetBox.Text = t.Mappings.SessionReset ?? "";
        WeeklyPctBox.Text = t.Mappings.WeeklyPct ?? "";
        WeeklyResetBox.Text = t.Mappings.WeeklyReset ?? "";
        IdentityBox.Text = t.Mappings.Identity ?? "";
        SessionResetKindCombo.SelectedItem = t.Mappings.SessionResetKind.ToString();
        WeeklyResetKindCombo.SelectedItem = t.Mappings.WeeklyResetKind.ToString();
    }

    private RequestTemplate BuildFromFields()
    {
        var template = new RequestTemplate
        {
            Url = UrlBox.Text?.Trim() ?? "",
            Method = string.IsNullOrWhiteSpace(MethodBox.Text) ? "GET" : MethodBox.Text.Trim(),
            AllowedHosts = (HostsBox.Text ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            Mappings = new MappingConfig
            {
                SessionPct = NullIfBlank(SessionPctBox.Text),
                SessionReset = NullIfBlank(SessionResetBox.Text),
                WeeklyPct = NullIfBlank(WeeklyPctBox.Text),
                WeeklyReset = NullIfBlank(WeeklyResetBox.Text),
                Identity = NullIfBlank(IdentityBox.Text),
                SessionResetKind = ParseKind(SessionResetKindCombo.SelectedItem),
                WeeklyResetKind = ParseKind(WeeklyResetKindCombo.SelectedItem),
            },
        };

        foreach (var line in (HeadersBox.Text ?? "").Split('\n'))
        {
            var trimmed = line.Trim();
            var idx = trimmed.IndexOf(':');
            if (idx <= 0) continue;
            var name = trimmed[..idx].Trim();
            var value = trimmed[(idx + 1)..].Trim();
            if (name.Length > 0) template.Headers[name] = value;
        }

        return template;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var template = BuildFromFields();
        var result = TemplateValidator.Validate(template);
        if (!result.IsValid)
        {
            ValidationText.Text = string.Join("  •  ", result.Errors);
            return;
        }

        _config.TemplatesBySource[_current.ToString()] = template;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static ResetKind ParseKind(object? selected) =>
        selected is string s && Enum.TryParse<ResetKind>(s, out var kind) ? kind : ResetKind.Timestamp;
}
