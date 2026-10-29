using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoManager.Contracts;
using RepoManager.Core.Agents;
using RepoManager.Core.Config;
using RepoManager.Core.Git;
using RepoManager.Core.Logs;
using RepoManager.Core.Processes;
using RepoManager.Core.State;

namespace RepoManager.App.Daemon;

/// <summary>Everything the daemon runs, minus the WinForms shell: core services plus the HTTP API.</summary>
public sealed class DaemonHost : IAsyncDisposable
{
    private static readonly TimeSpan AgentMaxAge = TimeSpan.FromHours(8);

    private WebApplication? _web;
    private readonly System.Threading.Timer _agentSweep;

    public string DataDir { get; }
    public string Token { get; }
    public int RequestedPort { get; }
    public int Port { get; private set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public GitService Git { get; } = new();
    public ConfigService Config { get; }
    public StateStore State { get; }
    public LogStore Logs { get; }
    public Supervisor Supervisor { get; }
    public EventHub Events { get; }
    /// <summary>Claude Code sessions on this machine, fed by <c>devm town-hook</c>.</summary>
    public AgentRegistry Agents { get; }
    public PermissionBroker Permissions { get; } = new();
    /// <summary>Agent Town XP, saved in the data directory.</summary>
    public TownProgress Progress { get; }
    /// <summary>Agent Town: past sessions read from Claude Code's transcripts and the desktop app's index.</summary>
    public SessionHistory History { get; }
    /// <summary>Agent Town: starts terminal and desktop sessions.</summary>
    public SessionStarter Starter { get; }

    /// <summary>Raised when a second launch or the CLI asks to show the window.</summary>
    public event Action? ActivateRequested;
    /// <summary>Raised when the installer asks the app to exit (update in place).</summary>
    public event Action? QuitRequested;

    /// <param name="launch">Starts a process (tests record instead); default starts it and reports success.</param>
    /// <param name="openLink">Opens a link with the shell (tests record instead).</param>
    public DaemonHost(string dataDir, int port, bool watchConfig = true, SupervisorOptions? options = null,
        Func<ProcessStartInfo, bool>? launch = null, Func<string, bool>? openLink = null)
    {
        DataDir = dataDir;
        RequestedPort = port;
        Directory.CreateDirectory(dataDir);
        Token = LoadOrCreateToken(dataDir);
        Config = new ConfigService(dataDir, Git, watchConfig);
        State = new StateStore(dataDir);
        Logs = new LogStore(Path.Combine(dataDir, "logs"));
        Supervisor = new Supervisor(Config, State, Logs, null, options);
        Agents = new AgentRegistry(cwd => Config.InstanceForPath(cwd) is { } p ? (p.Project, p.Worktree) : null, () => DateTimeOffset.Now);
        // The registry DTO carries the broker's prompts, so one "agent" event covers both.
        Permissions.PromptsChanged += id => Agents.SetPrompts(id, Permissions.PromptsFor(id));
        Agents.Ended += Permissions.ClearSession;
        Progress = new TownProgress(dataDir, () => DateTimeOffset.Now);
        Agents.Changed += s => Progress.SessionChanged(s);
        Agents.Ended += Progress.Forget;
        Events = new EventHub(Supervisor, Logs, Config, Agents, Progress);
        History = new SessionHistory(ClaudeProjectsDir(), DesktopSessionsDir(),
            cwd => Config.InstanceForPath(cwd) is { } p ? (p.Project, p.Worktree) : null);
        Starter = new SessionStarter(launch ?? Launch, openLink ?? WindowActivator.OpenLink, () => DateTimeOffset.Now,
            (socket, token, text) => AgentInbox.SendAsync(socket, token, text, CancellationToken.None));
        _agentSweep = new System.Threading.Timer(_ => Agents.Sweep(AgentMaxAge), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(System.Net.IPAddress.Loopback, RequestedPort));
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            var src = Protocol.Json;
            o.SerializerOptions.PropertyNamingPolicy = src.PropertyNamingPolicy;
            o.SerializerOptions.DefaultIgnoreCondition = src.DefaultIgnoreCondition;
            o.SerializerOptions.ReadCommentHandling = src.ReadCommentHandling;
            o.SerializerOptions.AllowTrailingCommas = true;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
            foreach (var c in src.Converters) o.SerializerOptions.Converters.Add(c);
        });
        _web = builder.Build();
        Api.Map(_web, this);
        await _web.StartAsync();

        var addresses = _web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Port = new Uri(addresses.First()).Port;
        WriteDaemonInfo();
    }

    public void RequestActivate() => ActivateRequested?.Invoke();
    public void RequestQuit() => QuitRequested?.Invoke();

    public string UiUrl => $"http://127.0.0.1:{Port}/#token={Token}";

    private void WriteDaemonInfo()
    {
        var info = new DaemonInfoDto(Protocol.Version, Environment.ProcessId, Port, StartedAt);
        File.WriteAllText(Protocol.DaemonInfoPath(DataDir), JsonSerializer.Serialize(info, Protocol.Json));
    }

    private static string LoadOrCreateToken(string dataDir)
    {
        var path = Protocol.TokenPath(dataDir);
        try
        {
            if (File.Exists(path))
            {
                var t = File.ReadAllText(path).Trim();
                if (t.Length >= 32) return t;
            }
        }
        catch (IOException) { }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(path, token);
        return token;
    }

    /// <summary>Stops every service (desired state kept for the next start) and the web server.</summary>
    public async ValueTask DisposeAsync()
    {
        await _agentSweep.DisposeAsync();
        Permissions.Dispose();
        try { await Supervisor.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
        try
        {
            var info = Protocol.DaemonInfoPath(DataDir);
            if (File.Exists(info)) File.Delete(info);
        }
        catch { }
        if (_web != null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _web.StopAsync(cts.Token); } catch { }
            await _web.DisposeAsync();
        }
        Events.Dispose();
        Supervisor.Dispose();
        Logs.Dispose();
        Config.Dispose();
    }

    private static bool Launch(ProcessStartInfo psi)
    {
        try
        {
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Claude Code's transcripts: &lt;CLAUDE_CONFIG_DIR or ~/.claude&gt;\projects.</summary>
    private static string ClaudeProjectsDir() => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "projects");

    /// <summary>The Claude desktop app's session index (one local_*.json per desktop session).</summary>
    private static string DesktopSessionsDir() =>
        Environment.GetEnvironmentVariable("REPOMANAGER_CLAUDE_DESKTOP_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions");
}
