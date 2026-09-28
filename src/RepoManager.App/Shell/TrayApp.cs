using System.Diagnostics;
using RepoManager.App.Daemon;
using RepoManager.Contracts;
using RepoManager.Core.Processes;

namespace RepoManager.App.Shell;

/// <summary>Tray icon, its menu, toasts and the main window's lifetime.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly DaemonHost _host;
    private readonly NotifyIcon _tray;
    private readonly Control _ui;
    private readonly System.Windows.Forms.Timer _refresh;
    private MainForm? _window;
    private string _lastStatus = "";
    private bool _quitting;

    public TrayApp(DaemonHost host, bool showWindow)
    {
        _host = host;
        _ui = new Control();
        _ui.CreateControl();
        _ = _ui.Handle; // bind to this thread for BeginInvoke

        _tray = new NotifyIcon
        {
            Text = "RepoManager",
            Icon = Icons.Tray(Icons.Status.Idle),
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        _tray.BalloonTipClicked += (_, _) => ShowWindow();

        host.Events.StatusChanged += _ => OnUi(UpdateIcon);
        host.Supervisor.Notified += n => OnUi(() => ShowNotification(n));
        host.ActivateRequested += () => OnUi(ShowWindow);
        host.QuitRequested += () => OnUi(() => Quit(confirm: false));

        // Fallback refresh: config reloads and health changes also change the icon.
        _refresh = new System.Windows.Forms.Timer { Interval = 5000 };
        _refresh.Tick += (_, _) => UpdateIcon();
        _refresh.Start();
        UpdateIcon();

        if (showWindow) ShowWindow();
    }

    private void OnUi(Action a)
    {
        if (_ui.IsDisposed) return;
        try { _ui.BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    private void ShowWindow()
    {
        if (_quitting) return;
        if (_window == null || _window.IsDisposed)
        {
            _window = new MainForm(_host);
            if (!_window.CanShow)
            {
                // No WebView2 runtime: use the default browser.
                OpenInBrowser(_host.UiUrl);
                _window.Dispose();
                _window = null;
                return;
            }
        }
        _window.Show();
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
        _window.BringToFront();
    }

    private void UpdateIcon()
    {
        var runners = _host.Supervisor.Runners;
        var status =
            runners.Any(r => r.State is ServiceState.Failed or ServiceState.Crashed) ? Icons.Status.Error :
            runners.Any(r => r.State == ServiceState.Unhealthy) ? Icons.Status.Warning :
            runners.Any(r => r.IsActive) ? Icons.Status.Ok : Icons.Status.Idle;
        var running = runners.Count(r => r.IsUp);
        var failed = runners.Count(r => r.State is ServiceState.Failed or ServiceState.Crashed);
        var text = $"RepoManager - {running} running" + (failed > 0 ? $", {failed} failed" : "");
        var key = status + text;
        if (key == _lastStatus) return;
        _lastStatus = key;
        var old = _tray.Icon;
        _tray.Icon = Icons.Tray(status);
        _tray.Text = text.Length > 63 ? text[..63] : text;
        if (old != null) Icons.Destroy(old);
    }

    private void ShowNotification(Notification n)
    {
        var project = _host.Config.FindProject(n.Project);
        if (project is { Notifications: false }) return;
        _tray.ShowBalloonTip(5000, n.Title, n.Message, n.IsError ? ToolTipIcon.Error : ToolTipIcon.Info);
    }

    private void BuildMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem("Open RepoManager", null, (_, _) => ShowWindow()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());

        var active = _host.Supervisor.Runners.Where(r => r.IsActive || r.State is ServiceState.Failed or ServiceState.Crashed).OrderBy(r => r.Id).ToList();
        if (active.Count == 0) menu.Items.Add(new ToolStripMenuItem("No services running") { Enabled = false });
        foreach (var r in active)
        {
            var item = new ToolStripMenuItem($"{r.Id}  ({StateLabel(r.State)}{(r.Port != null ? ", :" + r.Port : "")})");
            if (r.Url != null) item.DropDownItems.Add("Open in browser", null, (_, _) => OpenInBrowser(r.Url));
            item.DropDownItems.Add("Restart", null, (_, _) => _ = _host.Supervisor.RestartAsync(r.Id));
            item.DropDownItems.Add("Stop", null, (_, _) => _ = _host.Supervisor.StopAsync(r.Id));
            menu.Items.Add(item);
        }

        var tags = _host.Config.Registry.Projects.SelectMany(p => p.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
        if (tags.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            var start = new ToolStripMenuItem("Start group");
            var stop = new ToolStripMenuItem("Stop group");
            foreach (var t in tags)
            {
                start.DropDownItems.Add(t, null, (_, _) => _ = StartGroup(t, true));
                stop.DropDownItems.Add(t, null, (_, _) => _ = StartGroup(t, false));
            }
            menu.Items.Add(start);
            menu.Items.Add(stop);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit(confirm: true));
    }

    private async Task StartGroup(string tag, bool start)
    {
        var projects = _host.Config.Registry.Projects.Where(p => p.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).Select(p => p.EffectiveName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = _host.Supervisor.Runners.Where(r => !r.IsWorktree && projects.Contains(r.Instance.Project) && (!start || !r.Config.Disabled)).Select(r => r.Id).ToList();
        if (start) await _host.Supervisor.StartManyAsync(ids);
        else await _host.Supervisor.StopManyAsync(ids);
    }

    private static string StateLabel(ServiceState s) => s.ToString().ToLowerInvariant();

    public static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void Quit(bool confirm)
    {
        if (_quitting) return;
        var running = _host.Supervisor.Runners.Count(r => r.Process != null);
        if (confirm && running > 0)
        {
            var answer = MessageBox.Show(
                $"{running} service(s) are running. Quitting stops them now; they start again the next time RepoManager starts.\n\nQuit RepoManager?",
                "RepoManager", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;
        }
        _quitting = true;
        _tray.Text = "RepoManager - stopping services...";
        _window?.CloseForReal();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _window?.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
