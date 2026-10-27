using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RepoManager.App.Shell;

/// <summary>
/// Reads the average color of the screen just around a window: four thin strips a little outside
/// each edge, clear of the window's own drop shadow. Used to color the compact view after its surroundings.
/// </summary>
internal static class ScreenSampler
{
    /// <summary>Average color around <paramref name="window"/>, or null when nothing around it is on screen.</summary>
    public static Color? Around(Rectangle window, int gap, int thickness)
    {
        var desktop = SystemInformation.VirtualScreen;
        Rectangle[] strips =
        [
            new(window.Left, window.Top - gap - thickness, window.Width, thickness),
            new(window.Left, window.Bottom + gap, window.Width, thickness),
            new(window.Left - gap - thickness, window.Top, thickness, window.Height),
            new(window.Right + gap, window.Top, thickness, window.Height),
        ];
        long r = 0, g = 0, b = 0, n = 0;
        foreach (var strip in strips)
        {
            var area = Rectangle.Intersect(strip, desktop);
            if (area.Width <= 0 || area.Height <= 0) continue;
            using var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
            using (var gr = Graphics.FromImage(bmp)) gr.CopyFromScreen(area.Location, Point.Empty, area.Size);
            Accumulate(bmp, ref r, ref g, ref b, ref n);
        }
        return n == 0 ? null : Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
    }

    private static void Accumulate(Bitmap bmp, ref long r, ref long g, ref long b, ref long n)
    {
        var data = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var row = new byte[data.Width * 4];
            for (var y = 0; y < data.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var i = 0; i < row.Length; i += 4) // BGRx
                {
                    b += row[i];
                    g += row[i + 1];
                    r += row[i + 2];
                    n++;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
