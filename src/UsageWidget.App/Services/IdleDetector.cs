using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace UsageWidget.App.Services;

/// <summary>
/// §11 idle/locked detection: the poll loop slows to IdleCadence when the workstation is locked
/// or there has been no keyboard/mouse input for the configured threshold. Less background
/// traffic while the user is away means less rate-limit pressure on the providers.
/// </summary>
public static class IdleDetector
{
    private static volatile bool _locked;

    /// <summary>Call once at startup, on a thread with a message pump (WPF startup qualifies).</summary>
    public static void Initialize()
    {
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason == SessionSwitchReason.SessionLock) _locked = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock) _locked = false;
        };
    }

    public static bool IsIdleOrLocked(TimeSpan idleAfter) => _locked || IdleTime() >= idleAfter;

    private static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;

        // TickCount wraps ~25 days; unchecked int subtraction stays correct across the wrap.
        var idleMs = unchecked(Environment.TickCount - (int)info.dwTime);
        return idleMs > 0 ? TimeSpan.FromMilliseconds(idleMs) : TimeSpan.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
