using System.Text;
using System.Text.Json;
using RepoManager.Contracts;

namespace RepoManager.Cli;

/// <summary>devm command line. Exit codes: 0 ok, 1 error, 2 a service failed to start.</summary>
public static class Cli
{
    public const string Usage = """
        devm - RepoManager command line

        Usage (run inside a repo or worktree; short service names resolve from the current folder):
          devm status [--all]                      services of this checkout (--all: every project)
          devm start <svc|all> [--kill-owner] [--force]
          devm stop <svc|all>
          devm restart <svc|all> [--kill-owner] [--force]
          devm wait <svc> [--timeout 120]          block until running or failed
          devm logs <svc> [--tail 100] [--since 5m] [--grep text] [--follow]
          devm run <task> [--no-wait] [--timeout 600]
          devm url <svc>
          devm ports                               listening ports and their owners
          devm add [<path>]                        register a project (default: current folder)
          devm scan <dir>                          list repos under dir that can be added
          devm open [vscode|rider|explorer|terminal]
          devm ui                                  show the RepoManager window
          devm mcp                                 MCP server over stdio (for Claude Code)
          devm hook                                Claude Code PreToolUse hook (reads JSON on stdin)

        Full ids work anywhere: project/service, project@worktree/service.
        Every command accepts --json.
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var a = new Args(args);
        var cmd = a.Positional(0)?.ToLowerInvariant();

        if (cmd is null or "help" or "-h" or "--help" or "/?")
        {
            Console.WriteLine(Usage);
            return cmd == null ? 1 : 0;
        }
        if (cmd is "version" or "--version") { Console.WriteLine(Protocol.Version); return 0; }
        if (cmd == "hook") return await Hook.RunAsync();
        if (cmd == "mcp") return await Mcp.RunAsync(args.Skip(1).ToArray());

        using var client = new DaemonClient();
        try
        {
            await client.ConnectAsync(start: true);
            return cmd switch
            {
                "status" or "ls" or "ps" => await Status(client, a),
                "start" => await Action(client, a, "start"),
                "stop" => await Action(client, a, "stop"),
                "restart" => await Action(client, a, "restart"),
                "wait" => await Wait(client, a),
                "logs" or "log" => await Logs(client, a),
                "run" => await Run(client, a),
                "url" => await Url(client, a),
                "ports" => await Ports(client, a),
                "add" => await Add(client, a),
                "scan" => await Scan(client, a),
                "open" => await Open(client, a),
                "ui" => Print(a, await client.PostAsync<ActionResultDto>("api/app/activate", new { })),
                _ => Unknown(cmd),
            };
        }
        catch (DaemonUnavailableException ex)
        {
            Console.Error.WriteLine("devm: " + ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine("devm: " + ex.Message);
            return 1;
        }
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"devm: unknown command '{cmd}'. Run 'devm help'.");
        return 1;
    }

    private static string Cwd => Environment.CurrentDirectory;

    // ---- status ----

    private static async Task<int> Status(DaemonClient c, Args a)
    {
        var projects = await c.GetAsync<List<ProjectDto>>("api/projects?git=false");
        var here = await c.GetAsync<ResolveResultDto>($"api/resolve?target=all&cwd={Uri.EscapeDataString(Cwd)}");
        var all = a.Flag("all") || here.Instance == null;

        var instances = projects.SelectMany(p => p.Instances.Select(i => (Project: p, Instance: i)))
            .Where(x => all || string.Equals(x.Instance.Key, here.Instance, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (a.Json)
        {
            WriteJson(instances.Select(x => new { instance = x.Instance.Key, root = x.Instance.Root, errors = x.Project.Errors, services = x.Instance.Services, tasks = x.Instance.Tasks }));
            return 0;
        }
        if (instances.Count == 0)
        {
            Console.WriteLine(projects.Count == 0 ? "No projects registered. Run: devm add <path>" : "No services.");
            return 0;
        }

        var rows = new List<string[]> { new[] { "SERVICE", "STATE", "PORT", "URL", "PID", "UPTIME", "" } };
        foreach (var (project, inst) in instances)
        {
            foreach (var s in inst.Services)
                rows.Add([
                    s.Id,
                    s.Disabled ? "disabled" : StateText(s.State),
                    s.Port?.ToString() ?? "",
                    s.State is ServiceState.Running or ServiceState.Unhealthy ? s.Url ?? "" : "",
                    s.Pid?.ToString() ?? "",
                    s.StartedAt is { } t && s.State is ServiceState.Running or ServiceState.Unhealthy ? Uptime(DateTimeOffset.Now - t) : "",
                    s.State is ServiceState.Failed or ServiceState.Crashed ? s.LastError ?? "" : inst.Worktree != null && !s.WorktreeReady ? "not worktree-ready" : "",
                ]);
        }
        PrintTable(rows);
        foreach (var (project, _) in instances.DistinctBy(x => x.Project.Name))
            foreach (var e in project.Errors)
                Console.WriteLine($"{project.Name}: {e}");
        var tasks = instances.SelectMany(x => x.Instance.Tasks).ToList();
        if (tasks.Count > 0 && !all)
            Console.WriteLine("\nTasks: " + string.Join(", ", tasks.Select(t => t.Name + (t.Running ? " (running)" : t.LastExitCode is int code ? $" (exit {code})" : ""))));
        return 0;
    }

    private static string StateText(ServiceState s) => JsonNamingPolicy.CamelCase.ConvertName(s.ToString());

    private static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d{t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds}s" : $"{t.Seconds}s";

    // ---- start / stop / restart ----

    private static async Task<int> Action(DaemonClient c, Args a, string verb)
    {
        var target = a.Positional(1);
        if (target == null) { Console.Error.WriteLine($"usage: devm {verb} <service|all>"); return 1; }
        var resolved = await c.GetAsync<ResolveResultDto>($"api/resolve?target={Uri.EscapeDataString(target)}&cwd={Uri.EscapeDataString(Cwd)}");
        if (resolved.Error != null)
        {
            if (a.Json) WriteJson(ActionResultDto.Fail(resolved.Error));
            else Console.Error.WriteLine("devm: " + resolved.Error);
            return 1;
        }
        var res = await c.PostAsync<ActionResultDto>($"api/services/{verb}", new ServiceActionRequest
        {
            Target = target,
            Cwd = Cwd,
            KillOwner = a.Flag("kill-owner"),
            Force = a.Flag("force"),
        });
        return Print(a, res, failCode: verb == "stop" ? 1 : 2);
    }

    private static async Task<int> Wait(DaemonClient c, Args a)
    {
        var target = a.Positional(1);
        if (target == null) { Console.Error.WriteLine("usage: devm wait <service>"); return 1; }
        var timeout = a.Int("timeout") ?? 120;
        var res = await c.GetAsync<ActionResultDto>($"api/services/wait?target={Uri.EscapeDataString(target)}&cwd={Uri.EscapeDataString(Cwd)}&timeout={timeout}");
        return Print(a, res, failCode: 2);
    }

    // ---- logs ----

    private static async Task<int> Logs(DaemonClient c, Args a)
    {
        var target = a.Positional(1);
        var isTask = a.Flag("task");
        var query = $"api/logs?cwd={Uri.EscapeDataString(Cwd)}&tail={a.Int("tail") ?? 100}";
        if (target != null) query += "&target=" + Uri.EscapeDataString(target);
        if (a.Value("since") is { } since) query += "&since=" + Uri.EscapeDataString(since);
        if (a.Value("grep") is { } grep) query += "&grep=" + Uri.EscapeDataString(grep);
        if (isTask) query += "&task=true";
        var lines = await c.GetAsync<List<LogLineDto>>(query);
        var multi = lines.Select(l => l.Source).Distinct().Count() > 1 || target is null or "all";
        if (a.Json && !a.Flag("follow")) { WriteJson(lines); return 0; }
        foreach (var l in lines) WriteLine(l, multi, a.Json);

        if (!a.Flag("follow")) return 0;
        var resolved = await c.GetAsync<ResolveResultDto>($"api/resolve?target={Uri.EscapeDataString(target ?? "all")}&cwd={Uri.EscapeDataString(Cwd)}&tasks={isTask}");
        if (resolved.Error != null) { Console.Error.WriteLine("devm: " + resolved.Error); return 1; }
        var sources = isTask ? resolved.ServiceIds.Select(i => "task:" + i) : resolved.ServiceIds;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var grepText = a.Value("grep");
        try
        {
            await foreach (var e in c.EventsAsync(string.Join(",", sources), cts.Token))
            {
                if (e.Type != "log" || e.Payload is not JsonElement el) continue;
                var line = el.Deserialize<LogLineDto>(Protocol.Json);
                if (line == null || (grepText != null && !line.Text.Contains(grepText, StringComparison.OrdinalIgnoreCase))) continue;
                WriteLine(line, multi, a.Json);
            }
        }
        catch (OperationCanceledException) { }
        return 0;
    }

    private static void WriteLine(LogLineDto l, bool withSource, bool json)
    {
        if (json) { Console.WriteLine(JsonSerializer.Serialize(l, Protocol.Json)); return; }
        var prefix = withSource ? $"[{l.Source}] " : "";
        var marker = l.Stream switch { LogStream.Err => "! ", LogStream.Sys => "# ", _ => "" };
        Console.WriteLine($"{l.Timestamp:HH:mm:ss} {prefix}{marker}{l.Text}");
    }

    // ---- tasks ----

    private static async Task<int> Run(DaemonClient c, Args a)
    {
        var target = a.Positional(1);
        if (target == null) { Console.Error.WriteLine("usage: devm run <task>"); return 1; }
        var wait = !a.Flag("no-wait");
        var res = await c.PostAsync<ActionResultDto>($"api/tasks/run?wait={wait.ToString().ToLowerInvariant()}&timeout={a.Int("timeout") ?? 600}",
            new TaskRunRequest { Target = target, Cwd = Cwd });
        if (!a.Json && wait)
            foreach (var l in res.LogTail) Console.WriteLine(l);
        return Print(a, res with { LogTail = a.Json ? res.LogTail : [] });
    }

    // ---- misc ----

    private static async Task<int> Url(DaemonClient c, Args a)
    {
        var target = a.Positional(1);
        if (target == null) { Console.Error.WriteLine("usage: devm url <service>"); return 1; }
        var r = await c.GetAsync<ResolveResultDto>($"api/resolve?target={Uri.EscapeDataString(target)}&cwd={Uri.EscapeDataString(Cwd)}");
        if (r.Error != null) { Console.Error.WriteLine("devm: " + r.Error); return 1; }
        var services = await c.GetAsync<List<ServiceDto>>("api/services");
        var s = services.FirstOrDefault(x => r.ServiceIds.Contains(x.Id));
        if (a.Json) { WriteJson(new { id = s?.Id, url = s?.Url, port = s?.Port, state = s?.State }); return s?.Url != null ? 0 : 1; }
        if (s?.Url == null || s.State is not (ServiceState.Running or ServiceState.Unhealthy))
        {
            Console.Error.WriteLine($"devm: {r.ServiceIds.FirstOrDefault()} is not running (start it with: devm start {target})");
            return 1;
        }
        Console.WriteLine(s.Url);
        return 0;
    }

    private static async Task<int> Ports(DaemonClient c, Args a)
    {
        var ports = await c.GetAsync<List<PortDto>>("api/ports?cmd=true");
        if (a.Json) { WriteJson(ports); return 0; }
        var rows = new List<string[]> { new[] { "PORT", "PID", "PROCESS", "MANAGED BY", "COMMAND" } };
        foreach (var p in ports)
        {
            var cmd = p.CommandLine ?? "";
            if (cmd.Length > 80) cmd = cmd[..77] + "...";
            rows.Add([p.Port.ToString(), p.Pid.ToString(), p.ProcessName ?? "?", p.ManagedBy ?? "", cmd]);
        }
        PrintTable(rows);
        return 0;
    }

    private static async Task<int> Add(DaemonClient c, Args a)
    {
        var path = Path.GetFullPath(a.Positional(1) ?? Cwd);
        return Print(a, await c.PostAsync<ActionResultDto>("api/projects/add", new AddProjectRequest(path)));
    }

    private static async Task<int> Scan(DaemonClient c, Args a)
    {
        var dir = Path.GetFullPath(a.Positional(1) ?? Cwd);
        var found = await c.GetAsync<List<ScanResultDto>>("api/scan?dir=" + Uri.EscapeDataString(dir));
        if (a.Json) { WriteJson(found); return 0; }
        if (found.Count == 0) { Console.WriteLine("Nothing found."); return 0; }
        var rows = new List<string[]> { new[] { "NAME", "REGISTERED", "FOUND", "PATH" } };
        rows.AddRange(found.Select(f => new[] { f.Name, f.Registered ? "yes" : "", string.Join(",", f.Markers), f.Path }));
        PrintTable(rows);
        Console.WriteLine("\nAdd one with: devm add <path>");
        return 0;
    }

    private static async Task<int> Open(DaemonClient c, Args a)
    {
        var tool = a.Positional(1) ?? "explorer";
        var r = await c.GetAsync<ResolveResultDto>($"api/resolve?target=all&cwd={Uri.EscapeDataString(Cwd)}");
        if (r.Instance == null) { Console.Error.WriteLine("devm: " + (r.Error ?? "not inside a registered project")); return 1; }
        return Print(a, await c.PostAsync<ActionResultDto>("api/open", new OpenRequest(r.Instance, tool)));
    }

    // ---- output helpers ----

    private static int Print(Args a, ActionResultDto res, int failCode = 1)
    {
        if (a.Json) WriteJson(res);
        else if (res.Ok)
        {
            if (!string.IsNullOrEmpty(res.Message)) Console.WriteLine(res.Message);
        }
        else
        {
            if (!string.IsNullOrEmpty(res.Message)) Console.WriteLine(res.Message);
            Console.Error.WriteLine("devm: " + res.Error);
            if (res.LogTail.Count > 0)
            {
                Console.Error.WriteLine("--- last log lines ---");
                foreach (var l in res.LogTail) Console.Error.WriteLine(l);
            }
        }
        return res.Ok ? 0 : failCode;
    }

    public static void WriteJson(object? value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true }));

    private static void PrintTable(List<string[]> rows)
    {
        var widths = new int[rows[0].Length];
        foreach (var r in rows)
            for (var i = 0; i < r.Length; i++) widths[i] = Math.Max(widths[i], r[i].Length);
        foreach (var r in rows)
            Console.WriteLine(string.Join("  ", r.Select((c, i) => i == r.Length - 1 ? c : c.PadRight(widths[i]))).TrimEnd());
    }
}

/// <summary>Minimal parser: positionals, --flag, --name value, --name=value.</summary>
public sealed class Args
{
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase) { "tail", "since", "grep", "timeout" };

    public Args(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s.StartsWith("--") && s.Length > 2)
            {
                var name = s[2..];
                var eq = name.IndexOf('=');
                if (eq >= 0) _options[name[..eq]] = name[(eq + 1)..];
                else if (ValueOptions.Contains(name) && i + 1 < list.Count) _options[name] = list[++i];
                else _options[name] = null;
            }
            else if (s == "-f") _options["follow"] = null;
            else if (s == "-n" && i + 1 < list.Count) _options["tail"] = list[++i];
            else _positional.Add(s);
        }
    }

    public string? Positional(int i) => i < _positional.Count ? _positional[i] : null;
    public bool Flag(string name) => _options.ContainsKey(name);
    public string? Value(string name) => _options.TryGetValue(name, out var v) ? v : null;
    public int? Int(string name) => int.TryParse(Value(name), out var n) ? n : null;
    public bool Json => Flag("json");
}
