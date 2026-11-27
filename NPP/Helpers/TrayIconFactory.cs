using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Netch.App.Helpers;

internal static class TrayIconFactory
{
    public static Icon Create(bool proxyRunning)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            const int tileInset = 2;
            const int cornerDiameter = 14;
            const int farCorner = size - tileInset - cornerDiameter;
            using var tile = new GraphicsPath();
            tile.AddArc(tileInset, tileInset, cornerDiameter, cornerDiameter, 180, 90);
            tile.AddArc(farCorner, tileInset, cornerDiameter, cornerDiameter, 270, 90);
            tile.AddArc(farCorner, farCorner, cornerDiameter, cornerDiameter, 0, 90);
            tile.AddArc(tileInset, farCorner, cornerDiameter, cornerDiameter, 90, 90);
            tile.CloseFigure();
            using var tileBrush = new SolidBrush(Color.FromArgb(85, 70, 204));
            graphics.FillPath(tileBrush, tile);

            // The rim keeps the status color distinct when Windows scales the icon to 16 px.
            const int rimDiameter = 18;
            const int dotDiameter = 14;
            const float rimOffset = (size - rimDiameter) / 2f;
            const float dotOffset = (size - dotDiameter) / 2f;
            graphics.FillEllipse(Brushes.White, rimOffset, rimOffset, rimDiameter, rimDiameter);
            using var dot = new SolidBrush(proxyRunning
                ? Color.FromArgb(30, 166, 114)
                : Color.FromArgb(230, 65, 65));
            graphics.FillEllipse(dot, dotOffset, dotOffset, dotDiameter, dotDiameter);
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
