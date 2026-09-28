using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RepoManager.Contracts;

namespace RepoManager.Cli;

/// <summary>`devm mcp`: MCP server over stdio. stdout carries the protocol, so all logging goes to stderr.</summary>
public static class Mcp
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<DaemonClient>();
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new() { Name = "repomanager", Version = Protocol.Version })
            .WithStdioServerTransport()
            .WithTools<DevServerTools>();
        await builder.Build().RunAsync();
        return 0;
    }
}

[McpServerToolType]
public sealed class DevServerTools(DaemonClient client)
{
    private const string CwdHelp = "Folder used to resolve short service names (defaults to the session's working directory). Pass the repo or worktree you are working in.";

    private async Task Connect()
    {
        await client.ConnectAsync(start: true);
    }

    private static string Cwd(string? cwd) => string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd;

    private static string Json(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true });

    private static string Format(ActionResultDto r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Ok ? "OK: " + r.Message : "FAILED: " + r.Error);
        if (!r.Ok && !string.IsNullOrEmpty(r.Message)) sb.AppendLine(r.Message);
        if (r.LogTail.Count > 0)
        {
            sb.AppendLine("--- last log lines ---");
            foreach (var l in r.LogTail) sb.AppendLine(l);
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "list_services", ReadOnly = true)]
    [Description("List dev servers managed by RepoManager: id, state, port, url. By default only the checkout that contains cwd; set all=true for every project.")]
    public async Task<string> ListServices([Description(CwdHelp)] string? cwd = null, [Description("List every project and worktree")] bool all = false)
    {
        await Connect();
        var projects = await client.GetAsync<List<ProjectDto>>("api/projects?git=false");
        var here = await client.GetAsync<ResolveResultDto>($"api/resolve?target=all&cwd={Uri.EscapeDataString(Cwd(cwd))}");
        var list = projects.SelectMany(p => p.Instances)
            .Where(i => all || here.Instance == null || string.Equals(i.Key, here.Instance, StringComparison.OrdinalIgnoreCase))
            .Select(i => new
            {
                instance = i.Key,
                root = i.Root,
                services = i.Services.Select(s => new { s.Id, s.Name, state = s.State, s.Port, s.Url, s.LastError, s.WorktreeReady }),
                tasks = i.Tasks.Select(t => new { t.Id, t.Name, t.Running, t.LastExitCode }),
            });
        return Json(new { currentInstance = here.Instance, instances = list });
    }

    [McpServerTool(Name = "service_status", ReadOnly = true)]
    [Description("Status of one service: state, port, url, pid, last error, restart count.")]
    public async Task<string> ServiceStatus([Description("Service name (e.g. 'api') or full id ('project/api', 'project@worktree/api')")] string service, [Description(CwdHelp)] string? cwd = null)
    {
        await Connect();
        var r = await client.GetAsync<ResolveResultDto>($"api/resolve?target={Uri.EscapeDataString(service)}&cwd={Uri.EscapeDataString(Cwd(cwd))}");
        if (r.Error != null) return "FAILED: " + r.Error;
        var services = await client.GetAsync<List<ServiceDto>>("api/services");
        return Json(services.Where(s => r.ServiceIds.Contains(s.Id)));
    }

    [McpServerTool(Name = "start_service")]
    [Description("Start a managed dev server (and its dependencies) and wait until it is ready. Returns the URL, or the reason and last log lines on failure. Use this instead of running the dev command yourself.")]
    public async Task<string> StartService(
        [Description("Service name, full id, or 'all' for every service of the checkout")] string service,
        [Description(CwdHelp)] string? cwd = null,
        [Description("Kill a non-managed process that holds the service's port")] bool killOwner = false,
        [Description("Start in a worktree even though the service is not worktree-ready")] bool force = false,
        [Description("Start on a new free port (only for services that take their port from ${port:...})")] bool newPort = false)
    {
        await Connect();
        return Format(await client.PostAsync<ActionResultDto>("api/services/start",
            new ServiceActionRequest { Target = service, Cwd = Cwd(cwd), KillOwner = killOwner, Force = force, NewPort = newPort }));
    }

    [McpServerTool(Name = "stop_service", Destructive = true)]
    [Description("Stop a managed dev server and its whole process tree. Use this instead of taskkill/Stop-Process.")]
    public async Task<string> StopService([Description("Service name, full id, or 'all'")] string service, [Description(CwdHelp)] string? cwd = null)
    {
        await Connect();
        return Format(await client.PostAsync<ActionResultDto>("api/services/stop", new ServiceActionRequest { Target = service, Cwd = Cwd(cwd) }));
    }

    [McpServerTool(Name = "restart_service")]
    [Description("Restart a managed dev server (for example after changing backend code) and wait until it is ready again.")]
    public async Task<string> RestartService(
        [Description("Service name, full id, or 'all'")] string service,
        [Description(CwdHelp)] string? cwd = null,
        [Description("Kill a non-managed process that holds the service's port")] bool killOwner = false,
        [Description("Start on a new free port (only for services that take their port from ${port:...})")] bool newPort = false)
    {
        await Connect();
        return Format(await client.PostAsync<ActionResultDto>("api/services/restart",
            new ServiceActionRequest { Target = service, Cwd = Cwd(cwd), KillOwner = killOwner, NewPort = newPort }));
    }

    [McpServerTool(Name = "get_logs", ReadOnly = true)]
    [Description("Recent log lines of a service (kept after it stops or crashes). Lines are prefixed with ! for stderr and # for RepoManager messages.")]
    public async Task<string> GetLogs(
        [Description("Service name, full id, or 'all'")] string service,
        [Description(CwdHelp)] string? cwd = null,
        [Description("Number of lines (default 100)")] int tail = 100,
        [Description("Only lines newer than this, e.g. '30s', '5m'")] string? since = null,
        [Description("Only lines containing this text (case-insensitive)")] string? grep = null,
        [Description("Read the log of a task instead of a service")] bool task = false)
    {
        await Connect();
        var q = $"api/logs?target={Uri.EscapeDataString(service)}&cwd={Uri.EscapeDataString(Cwd(cwd))}&tail={tail}&task={task.ToString().ToLowerInvariant()}";
        if (since != null) q += "&since=" + Uri.EscapeDataString(since);
        if (grep != null) q += "&grep=" + Uri.EscapeDataString(grep);
        var lines = await client.GetAsync<List<LogLineDto>>(q);
        if (lines.Count == 0) return "(no log lines)";
        var multi = lines.Select(l => l.Source).Distinct().Count() > 1;
        var sb = new StringBuilder();
        foreach (var l in lines)
            sb.Append(l.Timestamp.ToString("HH:mm:ss")).Append(' ')
              .Append(multi ? $"[{l.Source}] " : "")
              .Append(l.Stream switch { LogStream.Err => "! ", LogStream.Sys => "# ", _ => "" })
              .AppendLine(StripAnsi(l.Text));
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "run_task")]
    [Description("Run a named one-off task of the project (for example publish, test, migrations) and wait for it to finish. Returns the exit code and the last output lines.")]
    public async Task<string> RunTask([Description("Task name or full id")] string task, [Description(CwdHelp)] string? cwd = null, [Description("Seconds to wait (default 600)")] int timeout = 600)
    {
        await Connect();
        return Format(await client.PostAsync<ActionResultDto>($"api/tasks/run?wait=true&timeout={timeout}", new TaskRunRequest { Target = task, Cwd = Cwd(cwd) }));
    }

    [McpServerTool(Name = "get_url", ReadOnly = true)]
    [Description("URL of a running service, for example to open it with a browser tool.")]
    public async Task<string> GetUrl([Description("Service name or full id")] string service, [Description(CwdHelp)] string? cwd = null)
    {
        await Connect();
        var r = await client.GetAsync<ResolveResultDto>($"api/resolve?target={Uri.EscapeDataString(service)}&cwd={Uri.EscapeDataString(Cwd(cwd))}");
        if (r.Error != null) return "FAILED: " + r.Error;
        var s = (await client.GetAsync<List<ServiceDto>>("api/services")).FirstOrDefault(x => r.ServiceIds.Contains(x.Id));
        if (s == null) return "FAILED: unknown service";
        return s.State is ServiceState.Running or ServiceState.Unhealthy && s.Url != null
            ? s.Url
            : $"FAILED: {s.Id} is {s.State.ToString().ToLowerInvariant()}; start it with start_service first";
    }

    private static string StripAnsi(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\x1B\[[0-9;?]*[ -/]*[@-~]", "");
}
