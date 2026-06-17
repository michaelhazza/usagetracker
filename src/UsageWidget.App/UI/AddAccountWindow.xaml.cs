using System.Windows;
using UsageWidget.Core.Accounts;
using UsageWidget.Core.Config;

namespace UsageWidget.App.UI;

public partial class AddAccountWindow : Window
{
    private readonly AdapterConfig _config;

    /// <summary>The created account + its secret, set only when the user saves successfully.</summary>
    public (Account Account, string Secret)? Result { get; private set; }

    private sealed record SourceOption(string Display, AccountSource Source);

    public AddAccountWindow(AdapterConfig config)
    {
        _config = config;
        InitializeComponent();

        SourceCombo.ItemsSource = new[]
        {
            new SourceOption("Claude (web token)", AccountSource.ClaudeWebToken),
            new SourceOption("Codex (pasted token)", AccountSource.CodexPastedToken),
            new SourceOption("Anthropic API key (advanced, opt-in)", AccountSource.AnthropicApiKey),
        };
        SourceCombo.SelectedIndex = 0;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Text?.Trim() ?? "";
        if (token.Length == 0)
        {
            ShowError("Please paste your token first (see the steps on the right).");
            return;
        }

        var source = ((SourceOption)SourceCombo.SelectedItem).Source;

        var account = new Account
        {
            Source = source,
            Nickname = string.IsNullOrWhiteSpace(NicknameBox.Text) ? null : NicknameBox.Text.Trim(),
        };

        Result = (account, token);
        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
