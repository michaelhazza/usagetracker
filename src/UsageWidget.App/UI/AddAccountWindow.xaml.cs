using System.Windows;
using UsageWidget.Core.Accounts;
using UsageWidget.Core.Config;
using UsageWidget.Core.Import;
using UsageWidget.Core.Security;

namespace UsageWidget.App.UI;

public partial class AddAccountWindow : Window
{
    /// <summary>
    /// Persists the account+secret and returns null on success, or a user-facing message describing
    /// why it couldn't be saved/reached. Runs while the dialog shows a progress indicator.
    /// </summary>
    private readonly Func<Account, string, Task<string?>> _saveAsync;

    private sealed record SourceOption(string Display, AccountSource Source);

    public AddAccountWindow(AdapterConfig config, Func<Account, string, Task<string?>> saveAsync)
    {
        _ = config;
        _saveAsync = saveAsync;
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

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        HideError();

        var curl = CurlBox.Text?.Trim() ?? "";
        if (curl.Length == 0)
        {
            ShowError("Paste the 'Copy as cURL' from DevTools first (steps above).");
            return;
        }

        var source = ((SourceOption)SourceCombo.SelectedItem).Source;

        // Step 1: parse the paste. Bad cURL is the most common failure — surface it clearly.
        Account account;
        string secret;
        try
        {
            var imported = CurlAccountImport.Build(curl, source);
            account = new Account
            {
                Source = source,
                Nickname = string.IsNullOrWhiteSpace(NicknameBox.Text) ? null : NicknameBox.Text.Trim(),
                Template = imported.Template,
            };
            secret = imported.Secret;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return;
        }

        // Step 2: save + first live check, with visible progress instead of a silent pause.
        SetBusy(true, "Saving and checking your usage…");
        string? error;
        try
        {
            error = await _saveAsync(account, secret);
        }
        catch (Exception ex)
        {
            error = "Couldn't save the account: " + Redactor.Redact(ex.Message);
        }
        SetBusy(false, null);

        if (error is null)
        {
            DialogResult = true;
            Close();
        }
        else
        {
            ShowError(error);
        }
    }

    private void SetBusy(bool busy, string? status)
    {
        StatusPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status ?? "";
        SaveButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        CurlBox.IsEnabled = !busy;
        NicknameBox.IsEnabled = !busy;
        SourceCombo.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
