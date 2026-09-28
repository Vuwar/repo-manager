using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RepoManager.App.Shell;

/// <summary>Draws the tray and window icons at runtime, so the state color needs no image files.</summary>
public static class Icons
{
    public enum Status { Idle, Ok, Warning, Error }

    private static readonly Color Brand = Color.FromArgb(0x4f, 0x7c, 0xff);

    private static Color ColorOf(Status s) => s switch
    {
        Status.Ok => Color.FromArgb(0x2e, 0xb8, 0x72),
        Status.Warning => Color.FromArgb(0xf0, 0xa2, 0x2e),
        Status.Error => Color.FromArgb(0xe5, 0x48, 0x4d),
        _ => Color.FromArgb(0x8a, 0x90, 0x9c),
    };

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon App()
    {
        var exe = Environment.ProcessPath;
        if (exe != null)
        {
            try
            {
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon != null) return icon;
            }
            catch { }
        }
        return Draw(32, null);
    }

    public static Icon Tray(Status status) => Draw(32, ColorOf(status));

    public static void Destroy(Icon icon)
    {
        try { DestroyIcon(icon.Handle); } catch { }
        icon.Dispose();
    }

    /// <summary>Rounded brand square with two stacked "server" bars; optional status dot.</summary>
    public static Icon Draw(int size, Color? dot)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var r = new RectangleF(1, 1, size - 2, size - 2);
            using (var path = Rounded(r, size * 0.22f))
            using (var b = new SolidBrush(Brand))
                g.FillPath(b, path);
            using var bar = new SolidBrush(Color.White);
            var w = size * 0.56f; var h = size * 0.16f; var x = (size - w) / 2;
            using (var p1 = Rounded(new RectangleF(x, size * 0.27f, w, h), h / 2)) g.FillPath(bar, p1);
            using (var p2 = Rounded(new RectangleF(x, size * 0.53f, w, h), h / 2)) g.FillPath(bar, p2);
            if (dot is Color c)
            {
                var d = size * 0.44f;
                var dr = new RectangleF(size - d, size - d, d - 0.5f, d - 0.5f);
                using var ring = new SolidBrush(Color.FromArgb(0x16, 0x18, 0x1d));
                g.FillEllipse(ring, dr);
                var inset = size * 0.06f;
                using var fill = new SolidBrush(c);
                g.FillEllipse(fill, RectangleF.Inflate(dr, -inset, -inset));
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
