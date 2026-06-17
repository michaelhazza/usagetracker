using System.Windows;

namespace UsageWidget.App.UI;

public partial class TokenPromptWindow : Window
{
    private string? _token;

    public TokenPromptWindow(string accountLabel)
    {
        InitializeComponent();
        HeaderText.Text = $"Re-paste token for {accountLabel}";
    }

    /// <summary>Shows the dialog and returns the entered token, or null if cancelled/empty.</summary>
    public static string? Prompt(string accountLabel)
    {
        var window = new TokenPromptWindow(accountLabel);
        return window.ShowDialog() == true ? window._token : null;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Text?.Trim() ?? "";
        if (token.Length == 0) return;
        _token = token;
        DialogResult = true;
        Close();
    }
}
