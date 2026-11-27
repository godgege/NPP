using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Netch.App.Helpers;

internal static class TrayIconFactory
{
    public static Icon Create(Icon appIcon, bool proxyRunning)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.DrawIcon(appIcon, new Rectangle(0, 0, size, size));

            // The icon's central diamond frames the status dot.
            const int dotSize = 6;
            var dotOffset = (size - dotSize) / 2f;
            using var dot = new SolidBrush(proxyRunning
                ? Color.FromArgb(30, 166, 114)
                : Color.FromArgb(230, 65, 65));
            graphics.FillEllipse(dot, dotOffset, dotOffset, dotSize, dotSize);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
