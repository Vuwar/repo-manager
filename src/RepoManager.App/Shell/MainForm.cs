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

    public bool CanShow { get; }

    public MainForm(DaemonHost host)
    {
        _host = host;
        Text = "RepoManager";
        Icon = Icons.App();
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1360, 860);
        MinimumSize = new Size(800, 500);
        BackColor = Color.FromArgb(0x16, 0x18, 0x1d);

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

    public void CloseForReal()
    {
        _reallyClose = true;
        Close();
    }
}
