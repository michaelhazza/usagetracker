using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using UsageWidget.Core.Config;

namespace UsageWidget.App.UI;

public partial class PopupWindow : Window
{
    public event Action<AccountRowViewModel>? RepasteRequested;
    public event Action<AccountRowViewModel>? ManageRequested;
    public event Action<AccountRowViewModel>? RemoveRequested;
    public event Action? AddAccountRequested;
    public event Action? OpenEditorRequested;
    public event Action? OpenHelpRequested;
    public event Action<AccountRowViewModel, int>? MoveRequested;

    /// <summary>Raised with the final bounds (Left, Top, Width) whenever positioning/resizing ends.</summary>
    public event Action<double, double, double>? BoundsChanged;

    /// <summary>Raised when the user toggles the pin (stay-on-top / no auto-hide).</summary>
    public event Action<bool>? PinnedChanged;

    private WindowBounds? _savedBounds;
    private bool _positioned;
    private bool _pinned;
    private DispatcherTimer? _topmostTimer;

    public PopupWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    /// <summary>Supply the last-known position before the window is first shown.</summary>
    public void RestorePosition(WindowBounds? bounds) => _savedBounds = bounds;

    /// <summary>Apply the persisted pin state before the window is first shown.</summary>
    public void RestorePinned(bool pinned)
    {
        _pinned = pinned;
        UpdatePinVisual();
        ApplyPinState();
    }

    // When pinned, behave like a fixed desktop widget: stay put and don't auto-hide. When unpinned,
    // behave like a tray flyout: hide on focus loss (saving position first).
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_pinned)
        {
            ReassertTopmost(); // re-assert so it never falls behind the window you just clicked
            return;
        }

        PersistBounds();
        Hide();
    }

    private void PersistBounds()
    {
        if (_positioned) BoundsChanged?.Invoke(Left, Top, Width);
    }

    /// <summary>
    /// WPF's Topmost alone is unreliable for this borderless/no-taskbar window — other apps can rise
    /// above it. While pinned, re-assert HWND_TOPMOST via Win32 on a timer so it truly never falls
    /// behind, even when another window is activated.
    /// </summary>
    private void ApplyPinState()
    {
        Topmost = true;

        _topmostTimer ??= CreateTopmostTimer();
        if (_pinned)
        {
            ReassertTopmost();
            _topmostTimer.Start();
        }
        else
        {
            _topmostTimer.Stop();
        }
    }

    private DispatcherTimer CreateTopmostTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (_pinned && IsVisible) ReassertTopmost();
        };
        return timer;
    }

    private void ReassertTopmost()
    {
        Topmost = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    // Drag the whole panel. Buttons mark MouseLeftButtonDown as handled, so clicks still work.
    // DragMove() blocks until the mouse is released, so persist the final spot right after.
    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
        PersistBounds();
    }

    // Right-edge grip → live-resize the width (clamped to Min/Max); persist when the drag ends.
    private void OnResizeWidth(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, MaxWidth);
    }

    private void OnResizeCompleted(object sender, DragCompletedEventArgs e) => PersistBounds();

    private void OnManageClick(object sender, RoutedEventArgs e) => RaiseRow(sender, ManageRequested);

    private void OnRemoveClick(object sender, RoutedEventArgs e) => RaiseRow(sender, RemoveRequested);

    private static void RaiseRow(object sender, Action<AccountRowViewModel>? handler)
    {
        if (sender is FrameworkElement { DataContext: AccountRowViewModel row }) handler?.Invoke(row);
    }

    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        UpdatePinVisual();
        ApplyPinState();
        if (_pinned) Activate();
        PinnedChanged?.Invoke(_pinned);
    }

    private static readonly System.Windows.Media.Brush PinOn = System.Windows.Media.Brushes.White;
    private static readonly System.Windows.Media.Brush PinOff =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x99, 0x99, 0x99));

    private void UpdatePinVisual()
    {
        PinButton.IsChecked = _pinned;
        PinButton.Foreground = _pinned ? PinOn : PinOff;
        PinButton.ToolTip = _pinned
            ? "Pinned (stays on top, won't auto-hide). Click to unpin."
            : "Pin: keep on top and stop auto-hiding";
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => RaiseMove(sender, -1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => RaiseMove(sender, +1);

    private void RaiseMove(object sender, int delta)
    {
        if (sender is FrameworkElement { DataContext: AccountRowViewModel row })
        {
            MoveRequested?.Invoke(row, delta);
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_positioned) return;
        _positioned = true;
        PlaceWindow();
    }

    /// <summary>
    /// Restore the saved spot if it's still on a connected monitor; otherwise anchor to the
    /// bottom-right of the primary work area (just above the tray).
    /// </summary>
    private void PlaceWindow()
    {
        // Restore a saved width first so the position math below uses the real size.
        if (_savedBounds is { Width: >= 1 } wb)
        {
            Width = Math.Clamp(wb.Width, MinWidth, MaxWidth);
            UpdateLayout();
        }

        if (_savedBounds is { } b && IsOnScreen(b.Left, b.Top))
        {
            Left = b.Left;
            Top = b.Top;
            return;
        }

        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 12;
        Top = wa.Bottom - ActualHeight - 12;
    }

    private bool IsOnScreen(double left, double top)
    {
        // Require a visible sliver (≥ 80px) of the window to land inside the virtual desktop.
        const double margin = 80;
        var vL = SystemParameters.VirtualScreenLeft;
        var vT = SystemParameters.VirtualScreenTop;
        var vR = vL + SystemParameters.VirtualScreenWidth;
        var vB = vT + SystemParameters.VirtualScreenHeight;
        return left + ActualWidth - margin >= vL && left + margin <= vR
            && top + ActualHeight - margin >= vT && top + margin <= vB;
    }

    private void OnAddAccountClick(object sender, RoutedEventArgs e) => AddAccountRequested?.Invoke();

    private void OnOpenEditorClick(object sender, RoutedEventArgs e) => OpenEditorRequested?.Invoke();

    private void OnOpenHelpClick(object sender, RoutedEventArgs e) => OpenHelpRequested?.Invoke();

    private void OnRepasteClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AccountRowViewModel row })
        {
            RepasteRequested?.Invoke(row);
        }
    }
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
