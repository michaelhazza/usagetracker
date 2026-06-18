using System.Windows;
using UsageWidget.Core.Accounts;
using UsageWidget.Core.Security;

namespace UsageWidget.App.UI;

public partial class ManageAccountWindow : Window
{
    private readonly Account _account;

    /// <summary>
    /// Persists the rename + optional re-pasted login. Args: (account, nickname, curlOrNull).
    /// Returns null on success, or a user-facing error to show inline.
    /// </summary>
    private readonly Func<Account, string?, string?, Task<string?>> _saveAsync;

    public ManageAccountWindow(Account account, Func<Account, string?, string?, Task<string?>> saveAsync)
    {
        _account = account;
        _saveAsync = saveAsync;
        InitializeComponent();

        NicknameBox.Text = account.Nickname ?? "";
        Title = $"Manage — {account.DisplayLabel(null)}";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        HideError();

        var nickname = NicknameBox.Text;
        var curl = CurlBox.Text?.Trim();
        if (string.IsNullOrEmpty(curl)) curl = null;

        SetBusy(true, curl is null ? "Saving…" : "Refreshing login and checking usage…");
        string? error;
        try
        {
            error = await _saveAsync(_account, nickname, curl);
        }
        catch (Exception ex)
        {
            error = Redactor.Redact(ex.Message);
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
        NicknameBox.IsEnabled = !busy;
        CurlBox.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
