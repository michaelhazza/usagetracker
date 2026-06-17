using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using UsageWidget.App.Services;
using UsageWidget.Core.Model;

namespace UsageWidget.App.Tray;

/// <summary>
/// Owns the WinForms <see cref="NotifyIcon"/>: dynamic color-coded glyph, left-click to open,
/// and the right-click menu (refresh, add account, settings, start-with-Windows, quit).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _notify;
    private Icon? _currentIcon;

    public event Action? OnLeftClick;
    public event Action? OnRefreshNow;
    public event Action? OnAddAccount;
    public event Action? OnSettings;
    public event Action? OnQuit;

    public TrayService()
    {
        _notify = new NotifyIcon
        {
            Visible = true,
            Text = "Usage Widget",
            ContextMenuStrip = BuildMenu(),
        };
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OnLeftClick?.Invoke();
        };
    }

    public void SetSeverity(double? worstPct, UsageSeverity? severity)
    {
        var next = TrayIconRenderer.Render(worstPct, severity);
        _notify.Icon = next;
        _currentIcon?.Dispose();
        _currentIcon = next;
        _notify.Text = worstPct is null ? "Usage Widget" : $"Worst session: {(int)worstPct}%";
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, (_, _) => OnRefreshNow?.Invoke());
        menu.Items.Add("Add account…", null, (_, _) => OnAddAccount?.Invoke());
        menu.Items.Add("Settings…", null, (_, _) => OnSettings?.Invoke());

        var startup = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = AutoStart.IsEnabled(),
        };
        startup.CheckedChanged += (_, _) => AutoStart.Set(startup.Checked);
        menu.Items.Add(startup);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => OnQuit?.Invoke());
        return menu;
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _currentIcon?.Dispose();
    }
}
