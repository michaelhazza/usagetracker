using System.Windows;
using UsageWidget.Core.Accounts;
using UsageWidget.Core.Config;
using UsageWidget.Core.Import;

namespace UsageWidget.App.UI;

public partial class AddAccountWindow : Window
{
    /// <summary>The created account (with its captured template) + its secret, set on save.</summary>
    public (Account Account, string Secret)? Result { get; private set; }

    private sealed record SourceOption(string Display, AccountSource Source);

    public AddAccountWindow(AdapterConfig config)
    {
        _ = config;
        InitializeComponent();

        SourceCombo.ItemsSource = new[]
        {
            new SourceOption("Claude (web)", AccountSource.ClaudeWebToken),
            new SourceOption("Codex", AccountSource.CodexPastedToken),
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
        var curl = CurlBox.Text?.Trim() ?? "";
        if (curl.Length == 0)
        {
            ShowError("Paste the 'Copy as cURL' from DevTools first (steps above).");
            return;
        }

        var source = ((SourceOption)SourceCombo.SelectedItem).Source;

        try
        {
            var imported = CurlAccountImport.Build(curl, source);
            var account = new Account
            {
                Source = source,
                Nickname = string.IsNullOrWhiteSpace(NicknameBox.Text) ? null : NicknameBox.Text.Trim(),
                Template = imported.Template,
            };

            Result = (account, imported.Secret);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
