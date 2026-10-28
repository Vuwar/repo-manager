using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using RepoManager.App.Daemon;
using RepoManager.Contracts;

namespace RepoManager.App.Shell;

/// <summary>The app window: a WebView2 hosting the React UI served by the daemon. Closing hides it to the tray.</summary>
public sealed class MainForm : Form
{
    private readonly DaemonHost _host;
    private readonly WebView2? _web;
    private bool _reallyClose;
    private bool _mini;
    private bool _wantTopmost;
    private Rectangle? _fullBounds;
    private Rectangle? _miniBounds;
    // Town mode: maximised (F11: borderless full screen); leaving restores what was there before.
    private bool _town;
    private (Rectangle Bounds, FormWindowState State)? _beforeTown;
    private static readonly Size FullMinimum = new(800, 500);
    private static readonly Color FullBack = Color.FromArgb(0x16, 0x18, 0x1d);

    // Compact view background, chosen in the UI: "glass" (acrylic), "match" (colors sampled
    // around the window) or "mixed" (both). Applies only while the window is compact.
    private string _ambient = "";
    private bool _glassOn;
    private Color? _sampled;
    private readonly System.Windows.Forms.Timer _sampleTimer = new() { Interval = 800 };
    private (bool Dark, Color Caption, Color Text, Color Border) _titleBar =
        (true, Color.FromArgb(0x11, 0x13, 0x17), Color.FromArgb(0xa3, 0xaa, 0xb7), Color.FromArgb(0x1f, 0x23, 0x2a));

    // Acrylic as a system backdrop needs Windows 11 22H2.
    private static bool AcrylicSupported => Environment.OSVersion.Version.Build >= 22621;

    public bool CanShow { get; }

    public MainForm(DaemonHost host)
    {
        _host = host;
        Text = "RepoManager";
        Icon = Icons.App();
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1360, 860);
        MinimumSize = FullMinimum;
        BackColor = FullBack;
        _sampleTimer.Tick += (_, _) => Sample();

        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            CanShow = true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            CanShow = false;
            return;
        }

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Protocol.DataDir(), "webview"));
            await _web!.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            // Links (service URLs) open in the default browser, not in a second app window.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                TrayApp.OpenInBrowser(e.Uri);
            };
            // The UI reports its layout: the compact view gets a small window that can stay on top.
            core.WebMessageReceived += (_, e) => OnUiMessage(e.WebMessageAsJson);
            core.DocumentTitleChanged += (_, _) => Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "RepoManager" : core.DocumentTitle;
            core.Navigate(_host.UiUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start the embedded browser: " + ex.Message + "\n\nOpening in your default browser instead.", "RepoManager");
            TrayApp.OpenInBrowser(_host.UiUrl);
            Hide();
        }
    }

    private void OnUiMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "mode":
                    SetMode(root.GetProperty("mode").GetString() ?? "");
                    break;
                case "fullscreen":
                    // F11 toggles; Esc sends on=false.
                    SetFullScreen(root.TryGetProperty("on", out var on) ? on.GetBoolean() : FormBorderStyle != FormBorderStyle.None);
                    break;
                case "topmost":
                    // Can arrive before the mode message; it applies once the window is compact.
                    _wantTopmost = root.GetProperty("on").GetBoolean();
                    TopMost = _mini && _wantTopmost;
                    break;
                case "titlebar":
                    _titleBar = (
                        root.GetProperty("dark").GetBoolean(),
                        ColorTranslator.FromHtml(root.GetProperty("caption").GetString()!),
                        ColorTranslator.FromHtml(root.GetProperty("text").GetString()!),
                        ColorTranslator.FromHtml(root.GetProperty("border").GetString()!));
                    RefreshTitleBar();
                    break;
                case "ambient":
                    _ambient = root.GetProperty("mode").GetString() ?? "";
                    _sampled = null;
                    ApplyAmbient();
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        {
            // Unknown message shape: ignore.
        }
    }

    private void SetMode(string mode)
    {
        // Leave the town first, so town and compact bounds never mix.
        if (mode != "town") SetTown(false);
        SetMini(mode == "mini");
        if (mode == "town") SetTown(true);
    }

    private void SetTown(bool town)
    {
        if (town == _town) return;
        _town = town;
        if (town)
        {
            _beforeTown = (WindowState == FormWindowState.Normal ? Bounds : RestoreBounds, WindowState);
            WindowState = FormWindowState.Maximized;
            return;
        }
        FormBorderStyle = FormBorderStyle.Sizable;
        if (_beforeTown is { } before)
        {
            WindowState = FormWindowState.Normal;
            Bounds = before.Bounds;
            WindowState = before.State == FormWindowState.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        }
        _beforeTown = null;
    }

    /// <summary>Town only: borderless and maximised (covers the taskbar), or back to a normal maximised window.</summary>
    private void SetFullScreen(bool on)
    {
        if (!_town || on == (FormBorderStyle == FormBorderStyle.None)) return;
        // Changing the border of a maximised window does not resize it; go through Normal.
        WindowState = FormWindowState.Normal;
        FormBorderStyle = on ? FormBorderStyle.None : FormBorderStyle.Sizable;
        WindowState = FormWindowState.Maximized;
    }

    private void SetMini(bool mini)
    {
        if (mini == _mini) return;
        _mini = mini;
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        if (mini)
        {
            _fullBounds = Bounds;
            MinimumSize = LogicalToDeviceUnits(new Size(260, 180));
            if (_miniBounds is { } saved && Screen.FromRectangle(saved).WorkingArea.IntersectsWith(saved))
            {
                Bounds = saved;
            }
            else
            {
                // First time: a narrow column at the right edge of the current screen.
                var area = Screen.FromControl(this).WorkingArea;
                var size = LogicalToDeviceUnits(new Size(320, 480));
                var margin = LogicalToDeviceUnits(16);
                Bounds = new Rectangle(area.Right - size.Width - margin, area.Bottom - size.Height - margin, size.Width, size.Height);
            }
            TopMost = _wantTopmost;
        }
        else
        {
            _miniBounds = Bounds;
            TopMost = false;
            MinimumSize = FullMinimum;
            if (_fullBounds is { } full) Bounds = full;
        }
        ApplyAmbient();
    }

    /// <summary>Turns acrylic and screen sampling on or off for the current mode and tells the UI what is on.</summary>
    private void ApplyAmbient()
    {
        var sample = _mini && _ambient is "match" or "mixed";
        SetGlass(_mini && AcrylicSupported && _ambient is "glass" or "mixed");
        _sampleTimer.Enabled = sample;
        if (sample) Sample();
        else _sampled = null;
        PostAmbient();
        RefreshTitleBar();
    }

    private void SetGlass(bool on)
    {
        if (on == _glassOn || !IsHandleCreated || _web is null) return;
        _glassOn = on;
        // The frame extended over the whole client area lets the backdrop show wherever the page is transparent.
        var margins = new Margins(on ? -1 : 0);
        DwmExtendFrameIntoClientArea(Handle, ref margins);
        SetDwm(DwmSystemBackdropType, on ? BackdropAcrylic : BackdropAuto);
        BackColor = on ? Color.Black : FullBack;
        _web.DefaultBackgroundColor = on ? Color.Transparent : FullBack;
    }

    private void Sample()
    {
        if (!Visible || WindowState == FormWindowState.Minimized) return;
        Color? color;
        try
        {
            color = ScreenSampler.Around(VisibleBounds(), LogicalToDeviceUnits(18), LogicalToDeviceUnits(6));
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or ArgumentException)
        {
            return; // Locked workstation or secure desktop: keep the last color.
        }
        if (color is not { } c) return;
        if (_sampled is { } old && Math.Abs(old.R - c.R) + Math.Abs(old.G - c.G) + Math.Abs(old.B - c.B) < 6) return;
        _sampled = c;
        PostAmbient();
        RefreshTitleBar();
    }

    // The town reports focus to the daemon so permission prompts wait for it only while the user is looking.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        PostWindowFocus(true);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        PostWindowFocus(false);
    }

    private void PostWindowFocus(bool focused)
    {
        if (_web?.CoreWebView2 is not { } core) return;
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "windowFocus", focused }));
    }

    private void PostAmbient()
    {
        if (_web?.CoreWebView2 is not { } core) return;
        int[]? rgb = _sampled is { } c ? [c.R, c.G, c.B] : null;
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "ambient", glass = _glassOn, rgb }));
    }

    /// <summary>Caption follows the background: clear over acrylic, the sampled color in match mode, else the UI theme.</summary>
    private void RefreshTitleBar()
    {
        if (!IsHandleCreated) return;
        if (_glassOn)
        {
            SetDwm(DwmUseImmersiveDarkMode, _titleBar.Dark ? 1 : 0);
            SetDwm(DwmCaptionColor, DwmColorDefault);
            SetDwm(DwmTextColor, DwmColorDefault);
            SetDwm(DwmBorderColor, DwmColorDefault);
        }
        else if (_mini && _sampled is { } c)
        {
            // Same rule as the UI's palette (web/src/lib/ambient.ts): dark surfaces get light text.
            var dark = Luminance(c) < 0.18;
            var caption = Darken(c, dark ? 0.16 : 0.05);
            PaintTitleBar(dark, caption, dark ? Color.FromArgb(0xe4, 0xe7, 0xed) : Color.FromArgb(0x1b, 0x1f, 0x27), caption);
        }
        else
        {
            PaintTitleBar(_titleBar.Dark, _titleBar.Caption, _titleBar.Text, _titleBar.Border);
        }
    }

    private static Color Darken(Color c, double t) => Color.FromArgb((int)(c.R * (1 - t)), (int)(c.G * (1 - t)), (int)(c.B * (1 - t)));

    private static double Luminance(Color c)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>The window as drawn, without the invisible resize borders that <see cref="Control.Bounds"/> includes.</summary>
    private Rectangle VisibleBounds()
    {
        return DwmGetWindowAttribute(Handle, DwmExtendedFrameBounds, out var r, Marshal.SizeOf<NativeRect>()) == 0
            ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
            : Bounds;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark defaults until the UI loads and sends its theme, so the bar never flashes white.
        RefreshTitleBar();
    }

    // DWM window attributes. Caption, text and border colors need Windows 11; older versions ignore them.
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;
    private const int DwmSystemBackdropType = 38;
    private const int DwmExtendedFrameBounds = 9;
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);
    private const int BackdropAuto = 0;
    private const int BackdropAcrylic = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins(int all)
    {
        public int Left = all, Right = all, Top = all, Bottom = all;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    private void PaintTitleBar(bool dark, Color caption, Color text, Color border)
    {
        if (!IsHandleCreated) return;
        SetDwm(DwmUseImmersiveDarkMode, dark ? 1 : 0);
        SetDwm(DwmCaptionColor, ColorRef(caption));
        SetDwm(DwmTextColor, ColorRef(text));
        SetDwm(DwmBorderColor, ColorRef(border));
    }

    private void SetDwm(int attribute, int value) => DwmSetWindowAttribute(Handle, attribute, ref value, sizeof(int));

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _sampleTimer.Dispose();
        base.Dispose(disposing);
    }

    public void CloseForReal()
    {
        _reallyClose = true;
        Close();
    }
}
