using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UsageWidget.App.UI;

public partial class PopupWindow : Window
{
    public PopupWindow() => InitializeComponent();

    // Hide (not close) when focus is lost, so the tray popup behaves like a flyout.
    private void OnDeactivated(object? sender, EventArgs e) => Hide();
}

/// <summary>Minimal bool→Visibility converter exposed as static instances for XAML x:Static use.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public static readonly BoolToVisibility Instance = new() { Inverse = false };
    public static readonly BoolToVisibility InverseInstance = new() { Inverse = true };

    public bool Inverse { get; init; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Inverse) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
