using System.Drawing;
using System.Runtime.InteropServices;

namespace RepoSyncV3.Views;

/// <summary>The app icon with a status dot in the corner (red / amber / green), for the tray.</summary>
public static class TrayIcon
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static IntPtr _last;

    /// <param name="tone">"error", "warning" or "success".</param>
    public static Icon With(Icon baseIcon, string tone)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawIcon(new Icon(baseIcon, 32, 32), new Rectangle(0, 0, 32, 32));
            var color = tone switch
            {
                "error" => Color.FromArgb(0xE5, 0x48, 0x4D),
                "warning" => Color.FromArgb(0xF5, 0xA5, 0x24),
                _ => Color.FromArgb(0x2E, 0xA0, 0x43),
            };
            using var ring = new SolidBrush(Color.White);
            using var dot = new SolidBrush(color);
            g.FillEllipse(ring, 17, 17, 15, 15);
            g.FillEllipse(dot, 19, 19, 11, 11);
        }
        var handle = bmp.GetHicon();
        var icon = Icon.FromHandle(handle);
        // the previous status icon is no longer shown: free its GDI handle
        if (_last != IntPtr.Zero) DestroyIcon(_last);
        _last = handle;
        return icon;
    }
}
