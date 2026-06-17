using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UsageWidget.Core.Model;

namespace UsageWidget.App.Tray;

/// <summary>
/// Draws the dynamic tray glyph: the worst current-session % over a color-coded background
/// (green &lt; 75, orange 75–90, red ≥ 90). GDI-rendered at the active DPI and converted to an
/// <see cref="Icon"/> for the NotifyIcon. Caller disposes the previous icon on swap.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayIconRenderer
{
    public static Icon Render(double? worstPct, UsageSeverity? severity, int size = 32)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var bg = new SolidBrush(BackgroundFor(severity));
            g.FillEllipse(bg, 0, 0, size - 1, size - 1);

            var text = worstPct is null ? "–" : $"{(int)Math.Round(worstPct.Value)}";
            using var font = new Font("Segoe UI", size * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.White);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, fg, new RectangleF(0, 0, size, size), sf);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone(); // detach from the GDI handle so we can free it
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static Color BackgroundFor(UsageSeverity? severity) => severity switch
    {
        UsageSeverity.Red => Color.FromArgb(0xD1, 0x3A, 0x3A),
        UsageSeverity.Orange => Color.FromArgb(0xE0, 0x8A, 0x1E),
        UsageSeverity.Green => Color.FromArgb(0x2E, 0x9E, 0x44),
        _ => Color.FromArgb(0x6B, 0x6B, 0x6B), // no data
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
