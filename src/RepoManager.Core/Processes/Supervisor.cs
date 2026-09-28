using RepoManager.Contracts;
using RepoManager.Core.Config;
using RepoManager.Core.Logs;
using RepoManager.Core.Ports;
using RepoManager.Core.State;

namespace RepoManager.Core.Processes;

public sealed record StartOptions(bool KillOwner = false, bool Force = false);

public sealed record Notification(string Project, string Title, string Message, bool IsError);

public sealed class SupervisorOptions
{
    public TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(10);
    public int HealthFailuresForUnhealthy { get; init; } = 3;
    public TimeSpan UnhealthyNotifyAfter { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan AliveGrace { get; init; } = TimeSpan.FromSeconds(3);
    public int CrashLimit { get; init; } = 5;
    public TimeSpan CrashWindow { get; init; } = TimeSpan.FromSeconds(60);
    public Func<int, TimeSpan> Backoff { get; init; } = n => TimeSpan.FromSeconds(Math.Min(16, Math.Pow(2, n)));
}

/// <summary>Owns every managed process. Start/stop/restart, dependencies, ports, readiness, health, auto restart, tasks.</summary>
public sealed class Supervisor : IDisposable
{
    private readonly ConfigService _config;
    private readonly StateStore _state;
    private readonly LogStore _logs;
    private readonly PortAllocator _allocator;
    private readonly SupervisorOptions _options;
    private readonly HttpClient _http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) }) { Timeout = TimeSpan.FromSeconds(3) };
    private readonly object _lock = new();
    private readonly Dictionary<string, ServiceRunner> _runners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskRunner> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event Action<ServiceDto>? ServiceChanged;
    public event Action<TaskDto>? TaskChanged;
    public event Action<Notification>? Notified;

    public Supervisor(ConfigService config, StateStore state, LogStore logs, PortAllocator? allocator = null, SupervisorOptions? options = null)
    {
        _config = config;
        _state = state;
        _logs = logs;
        _allocator = allocator ?? new PortAllocator();
        _options = options ?? new SupervisorOptions();
        Sync();
        _config.Changed += Sync;
    }

    // ---- Queries ----

    public IReadOnlyList<ServiceRunner> Runners { get { lock (_lock) return [.. _runners.Values]; } }

    public ServiceRunner? Find(string id) { lock (_lock) return _runners.GetValueOrDefault(id); }

    public TaskRunner? FindTask(string id) { lock (_lock) return _tasks.GetValueOrDefault(id); }

    public IReadOnlyList<TaskRunner> Tasks { get { lock (_lock) return [.. _tasks.Values]; } }

    public ServiceDto ToDto(ServiceRunner r) => r.ToDto(_state.IsDesired(r.Id));

    /// <summary>PID → service id for every process in every running service tree.</summary>
    public Dictionary<int, string> ManagedPids()
    {
        var map = new Dictionary<int, string>();
        foreach (var r in Runners)
        {
            var p = r.Process;
            if (p == null) continue;
            foreach (var pid in p.TreePids()) map[pid] = r.Id;
            map[p.Pid] = r.Id;
        }
        foreach (var t in Tasks)
        {
            var p = t.Process;
            if (p == null) continue;
            foreach (var pid in p.TreePids()) map[pid] = t.Id;
        }
        return map;
    }

    // ---- Config sync ----

    private void Sync()
    {
        if (_disposed) return;
        var instances = _config.Instances;
        var removed = new List<ServiceRunner>();
        lock (_lock)
        {
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var liveTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in instances)
            {
                foreach (var svc in inst.Services.Values)
                {
                    var id = Ids.Service(inst.InstanceKey, svc.Name);
                    live.Add(id);
                    if (_runners.TryGetValue(id, out var r)) { r.Instance = inst; r.Config = svc; }
                    else _runners[id] = new ServiceRunner(id, inst, svc, _logs.Get(id));
                }
                foreach (var task in inst.Tasks.Values)
                {
                    var id = Ids.Service(inst.InstanceKey, task.Name);
                    liveTasks.Add(id);
                    if (_tasks.TryGetValue(id, out var t)) { t.Instance = inst; t.Config = task; }
                    else _tasks[id] = new TaskRunner(id, inst, task, _logs.Get("task:" + id));
                }
            }
            foreach (var id in _runners.Keys.Where(k => !live.Contains(k)).ToList())
            {
                removed.Add(_runners[id]);
                _runners.Remove(id);
            }
            foreach (var id in _tasks.Keys.Where(k => !liveTasks.Contains(k)).ToList())
            {
                _tasks[id].Process?.Dispose();
                _tasks.Remove(id);
            }
        }
        // Services whose config disappeared (worktree deleted, service renamed) must not keep running unmanaged.
        foreach (var r in removed)
            if (r.Process != null) _ = StopRunnerAsync(r, clearDesired: false, reason: "service removed from config");
        _state.Prune(instances.Select(i => i.InstanceKey));
    }

    // ---- Target resolution ----

    /// <summary>
    /// Resolves a CLI/MCP target: full id, "all", or a short service name relative to <paramref name="cwd"/>.
    /// </summary>
    public ResolveResultDto Resolve(string? target, string? cwd, bool tasks = false)
    {
        target = target?.Trim();
        ProjectConfig? inst = null;
        if (!string.IsNullOrEmpty(cwd)) inst = _config.InstanceForPath(cwd);

        if (!string.IsNullOrEmpty(target) && target.Contains('/'))
        {
            var exists = tasks ? FindTask(target) != null : Find(target) != null;
            if (!exists && target.EndsWith("/all", StringComparison.OrdinalIgnoreCase))
            {
                var key = target[..^4];
                var i = _config.FindInstance(key);
                if (i != null) return AllOf(i, tasks);
            }
            if (!exists)
            {
                // "project/svc" typed from inside a worktree of that project means the worktree copy.
                if (Ids.TryParse(target, out var p, out var wt, out var n) && wt == null && inst != null &&
                    string.Equals(inst.Project, p, StringComparison.OrdinalIgnoreCase) && inst.Worktree != null)
                {
                    var wid = Ids.Service(inst.InstanceKey, n);
                    if (tasks ? FindTask(wid) != null : Find(wid) != null)
                        return new ResolveResultDto { Project = inst.Project, Instance = inst.InstanceKey, Worktree = inst.Worktree, ServiceIds = [wid] };
                }
                return new ResolveResultDto { Error = $"unknown {(tasks ? "task" : "service")} '{target}'" };
            }
            Ids.TryParse(target, out var proj, out var worktree, out _);
            return new ResolveResultDto { Project = proj, Worktree = worktree, Instance = Ids.Instance(proj, worktree), ServiceIds = [Canonical(target, tasks)] };
        }

        if (inst == null)
        {
            // Short name outside any repo: accept it when exactly one main checkout has it.
            if (!string.IsNullOrEmpty(target) && !target.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var matches = (tasks ? Tasks.Select(t => (t.Id, t.Instance)) : Runners.Select(r => (r.Id, r.Instance)))
                    .Where(x => x.Instance.Worktree == null && x.Id.EndsWith("/" + target, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1)
                    return new ResolveResultDto { Project = matches[0].Instance.Project, Instance = matches[0].Instance.InstanceKey, ServiceIds = [matches[0].Id] };
                if (matches.Count > 1)
                    return new ResolveResultDto { Error = $"'{target}' is ambiguous: {string.Join(", ", matches.Select(m => m.Id))}" };
            }
            return new ResolveResultDto { Error = cwd == null ? "no project given; use a full id like project/service" : $"'{cwd}' is not inside a registered project (run: devm add <path>)" };
        }

        if (string.IsNullOrEmpty(target) || target.Equals("all", StringComparison.OrdinalIgnoreCase))
            return AllOf(inst, tasks);

        var id = Ids.Service(inst.InstanceKey, target);
        var found = tasks ? FindTask(id) != null : Find(id) != null;
        return found
            ? new ResolveResultDto { Project = inst.Project, Instance = inst.InstanceKey, Worktree = inst.Worktree, ServiceIds = [Canonical(id, tasks)] }
            : new ResolveResultDto
            {
                Project = inst.Project, Instance = inst.InstanceKey, Worktree = inst.Worktree,
                Error = $"unknown {(tasks ? "task" : "service")} '{target}' in {inst.InstanceKey} (known: {string.Join(", ", tasks ? inst.Tasks.Keys : inst.Services.Keys)})",
            };
    }

    private string Canonical(string id, bool tasks) => tasks ? FindTask(id)?.Id ?? id : Find(id)?.Id ?? id;

    private ResolveResultDto AllOf(ProjectConfig inst, bool tasks) => new()
    {
        Project = inst.Project,
        Instance = inst.InstanceKey,
        Worktree = inst.Worktree,
        ServiceIds = tasks
            ? inst.Tasks.Keys.Select(n => Ids.Service(inst.InstanceKey, n)).ToList()
            : inst.Services.Values.Where(s => !s.Disabled).Select(s => Ids.Service(inst.InstanceKey, s.Name)).ToList(),
    };

    // ---- Start ----

    public async Task<ActionResultDto> StartAsync(string id, StartOptions? options = null, CancellationToken ct = default)
    {
        options ??= new StartOptions();
        var r = Find(id);
        if (r == null) return ActionResultDto.Fail($"unknown service '{id}'");
        return await StartWithDepsAsync(r, options, [], ct);
    }

    public async Task<ActionResultDto> StartManyAsync(IEnumerable<string> ids, StartOptions? options = null)
    {
        var results = new List<(string Id, ActionResultDto Result)>();
        // Sequential in dependency order: deps get started once and dependents find them running.
        var runners = ids.Select(Find).Where(r => r != null).Cast<ServiceRunner>().ToList();
        var ordered = new List<ServiceRunner>();
        foreach (var g in runners.GroupBy(r => r.InstanceKey))
        {
            var inst = g.First().Instance;
            foreach (var n in ConfigLoader.StartOrder(inst, g.Select(r => r.Name)))
            {
                var rr = Find(Ids.Service(inst.InstanceKey, n));
                if (rr != null && g.Contains(rr)) ordered.Add(rr);
            }
        }
        await Task.WhenAll(ordered.GroupBy(r => r.InstanceKey).Select(async g =>
        {
            foreach (var r in g)
            {
                var res = await StartWithDepsAsync(r, options ?? new StartOptions(), [], default);
                lock (results) results.Add((r.Id, res));
            }
        }));
        return Combine(results, "started");
    }

    private static ActionResultDto Combine(List<(string Id, ActionResultDto Result)> results, string verb)
    {
        if (results.Count == 0) return ActionResultDto.Fail("nothing to do");
        var failed = results.Where(r => !r.Result.Ok).ToList();
        if (failed.Count == 0)
            return new ActionResultDto { Ok = true, Message = string.Join("\n", results.Select(r => r.Result.Message)), Affected = results.Select(r => r.Id).ToList() };
        return new ActionResultDto
        {
            Ok = false,
            Error = string.Join("\n", failed.Select(f => $"{f.Id}: {f.Result.Error}")),
            Message = string.Join("\n", results.Where(r => r.Result.Ok).Select(r => r.Result.Message)),
            Affected = results.Where(r => r.Result.Ok).Select(r => r.Id).ToList(),
            LogTail = failed[0].Result.LogTail,
        };
    }

    private async Task<ActionResultDto> StartWithDepsAsync(ServiceRunner r, StartOptions options, HashSet<string> chain, CancellationToken ct)
    {
        if (!chain.Add(r.Id)) return ActionResultDto.Fail("dependency cycle");
        var inst = r.Instance;
        if (!inst.Valid) return ActionResultDto.Fail($"config of {inst.InstanceKey} has errors: {string.Join("; ", inst.Errors)}");
        if (r.Config.Disabled) return ActionResultDto.Fail($"{r.Id} is disabled");
        if (r.IsWorktree && !r.WorktreeReady && !options.Force)
            return ActionResultDto.Fail($"{r.Id} is not worktree-ready: it uses a fixed port and no ${{port:{r.Name}}} in its args/env, so a second copy would clash. " +
                                        "Make the service read its port from an env variable or argument, or pass --force.");

        var deps = r.Config.DependsOn.Select(d => Find(Ids.Service(inst.InstanceKey, d))).Where(d => d != null).Cast<ServiceRunner>().ToList();
        if (deps.Any(d => !d.IsUp))
        {
            if (!r.IsActive) SetState(r, ServiceState.WaitingDeps);
            foreach (var d in deps.Where(d => !d.IsUp))
            {
                var res = await StartWithDepsAsync(d, options with { Force = options.Force }, chain, ct);
                if (!res.Ok)
                {
                    if (r.State == ServiceState.WaitingDeps) { r.LastError = $"dependency {d.Id} failed"; SetState(r, ServiceState.Failed); }
                    return ActionResultDto.Fail($"dependency {d.Id} failed: {res.Error}", res.LogTail);
                }
            }
        }
        return await StartRunnerAsync(r, options, ct);
    }

    private async Task<ActionResultDto> StartRunnerAsync(ServiceRunner r, StartOptions options, CancellationToken ct)
    {
        await r.Gate.WaitAsync(ct);
        try
        {
            if (r.IsUp) return ActionResultDto.Success($"{r.Id} already running{(r.Url != null ? " at " + r.Url : "")}", r.Id);

            r.LastError = null;
            r.StopRequested = false;
            r.RunCts?.Dispose();
            r.RunCts = new CancellationTokenSource();
            var token = r.RunCts.Token;

            // Ports and variables.
            var portResult = DecidePort(r, options);
            if (portResult.Error != null) return Fail(r, portResult.Error);
            r.Port = portResult.Port;
            var ctx = VariableContext(r.Instance, r);
            var cfg = r.Config;
            var cwd = Path.GetFullPath(Path.Combine(r.Instance.Root, Variables.Resolve(cfg.Cwd, ctx)));
            var env = BuildEnv(cfg.Env, ctx);
            r.Url = ResolveUrl(cfg, ctx, r.Port);

            r.Log.Append(LogStream.Sys, $"--- starting {r.Id}{(r.Port != null ? " on port " + r.Port : "")} ---");

            // Prepare steps.
            if (cfg.Prepare.Count > 0)
            {
                SetState(r, ServiceState.Preparing);
                foreach (var step in cfg.Prepare)
                {
                    token.ThrowIfCancellationRequested();
                    if (step.If != null)
                    {
                        var cond = ConfigLoader.ParseCondition(step.If)!.Value;
                        var exists = Path.Exists(Path.Combine(cwd, Variables.Resolve(cond.Path, ctx)));
                        if (exists == cond.Negate) continue;
                    }
                    var cmd = Variables.Resolve(step.Run, ctx);
                    r.Log.Append(LogStream.Sys, $"> {cmd}");
                    using var prep = ManagedProcess.Start(new ProcessSpec { CommandLine = cmd, WorkingDirectory = cwd, Environment = env }, r.Log.Append);
                    int code;
                    using (token.Register(() => _ = prep.KillAsync()))
                        code = await prep.Exited;
                    token.ThrowIfCancellationRequested();
                    if (code != 0) return Fail(r, $"prepare step '{cmd}' exited with code {code}");
                }
            }

            // Spawn.
            SetState(r, ServiceState.Starting);
            var commandLine = ManagedProcess.BuildCommandLine(Variables.Resolve(cfg.Command, ctx), cfg.Args.Select(a => Variables.Resolve(a, ctx)));
            r.ResolvedCommand = commandLine;
            r.Log.Append(LogStream.Sys, $"> {commandLine}");
            ManagedProcess proc;
            try
            {
                proc = ManagedProcess.Start(new ProcessSpec { CommandLine = commandLine, WorkingDirectory = cwd, Environment = env }, r.Log.Append);
            }
            catch (Exception ex)
            {
                return Fail(r, "could not start: " + ex.Message);
            }
            r.Process = proc;
            r.StartedAt = DateTimeOffset.Now;
            r.LastExitCode = null;
            _ = WatchExitAsync(r, proc);

            var ready = await WaitReadyAsync(r, proc, ctx, token);
            if (ready != null)
            {
                if (r.Process == proc) { await proc.KillAsync(); r.Process = null; proc.Dispose(); }
                return Fail(r, ready);
            }

            r.UnhealthySince = null;
            r.UnhealthyNotified = false;
            SetState(r, ServiceState.Running);
            _state.SetDesired(r.Id, true);
            r.Log.Append(LogStream.Sys, $"--- {r.Id} is running{(r.Url != null ? " at " + r.Url : "")} ---");
            if (cfg.Health != null) _ = HealthLoopAsync(r, proc, Variables.Resolve(cfg.Health, ctx), token);
            return ActionResultDto.Success($"{r.Id} running{(r.Url != null ? " at " + r.Url : "")}", r.Id);
        }
        catch (OperationCanceledException)
        {
            return ActionResultDto.Fail($"{r.Id}: start cancelled");
        }
        finally
        {
            r.Gate.Release();
        }
    }

    private ActionResultDto Fail(ServiceRunner r, string error)
    {
        r.LastError = error;
        r.Log.Append(LogStream.Sys, "--- failed: " + error + " ---");
        SetState(r, ServiceState.Failed);
        return ActionResultDto.Fail($"{r.Id}: {error}", r.Log.TailText(50));
    }

    private sealed record PortDecision(int? Port, string? Error);

    private PortDecision DecidePort(ServiceRunner r, StartOptions options)
    {
        var cfg = r.Config;
        if (cfg.Port == null && !cfg.AutoPort) return new(null, null);

        var taken = TakenPorts(r);
        if (r.IsWorktree || cfg.AutoPort)
        {
            // Main checkout: configured port when free. Worktree: never the main checkout's fixed port.
            if (!r.IsWorktree && cfg.Port is int preferred && !taken.Contains(preferred) && PortInspector.IsFree(preferred))
                return new(preferred, null);
            var sticky = _state.GetPort(r.InstanceKey, r.Name);
            if (sticky is int s && !taken.Contains(s) && PortInspector.IsFree(s)) return new(s, null);
            if (r.IsWorktree && !r.WorktreeReady && cfg.Port is int fixedPort)
                return CheckFixed(r, fixedPort, options); // --force on a worktree that cannot move its port
            taken.UnionWith(_state.AllStickyPorts());
            var p = _allocator.Allocate(taken);
            if (p == null) return new(null, $"no free port in {PortAllocator.RangeStart}-{PortAllocator.RangeEnd}");
            _state.SetPort(r.InstanceKey, r.Name, p.Value);
            return new(p, null);
        }
        return CheckFixed(r, cfg.Port!.Value, options);
    }

    private PortDecision CheckFixed(ServiceRunner r, int port, StartOptions options)
    {
        var owners = PortInspector.OwnersOf(port);
        // Entries of processes that just died linger briefly; give them a moment to go away.
        for (var i = 0; i < 30 && owners.Count > 0 && owners.All(o => PortInspector.ProcessName(o.Pid) == null); i++)
        {
            Thread.Sleep(100);
            owners = PortInspector.OwnersOf(port);
        }
        if (owners.Count == 0) return new(port, null);
        var managed = ManagedPids();
        foreach (var o in owners)
            if (managed.TryGetValue(o.Pid, out var owner))
                return new(null, $"port {port} is already used by {owner} (managed by RepoManager)");
        if (!options.KillOwner)
        {
            // Several processes can share a port on different addresses (0.0.0.0 vs [::1]); name them all.
            var described = owners.DistinctBy(o => o.Pid).Select(o =>
            {
                var name = PortInspector.ProcessName(o.Pid) ?? "?";
                var cmd = PortInspector.CommandLine(o.Pid);
                return $"PID {o.Pid} ({name}) on {o.Address}{(cmd != null ? ": " + cmd : "")}";
            });
            return new(null, $"port {port} is in use by {string.Join("; ", described)}. Use --kill-owner to kill it.");
        }
        foreach (var o in owners.DistinctBy(o => o.Pid))
        {
            var info = ProcessInfo.Get(o.Pid);
            if (!info.Mine || info.System)
                return new(null, $"port {port} is held by PID {o.Pid} ({PortInspector.ProcessName(o.Pid)}), a Windows or system process; RepoManager will not kill it. Change the service's port.");
        }
        foreach (var o in owners.DistinctBy(o => o.Pid))
        {
            r.Log.Append(LogStream.Sys, $"killing PID {o.Pid} ({PortInspector.ProcessName(o.Pid)}) holding port {port}");
            PortInspector.KillTree(o.Pid);
        }
        var until = DateTime.UtcNow.AddSeconds(5);
        while (PortInspector.IsListening(port) && DateTime.UtcNow < until) Thread.Sleep(100);
        return PortInspector.IsListening(port) ? new(null, $"port {port} is still in use after killing its owner") : new(port, null);
    }

    /// <summary>Ports in use by other active managed services (they may not be listening yet).</summary>
    private HashSet<int> TakenPorts(ServiceRunner except) =>
        Runners.Where(x => x != except && x.IsActive && x.Port != null).Select(x => x.Port!.Value).ToHashSet();

    /// <summary>Ports for ${port:...}: actual ports of running services, planned ports for the rest.</summary>
    private Variables.Context VariableContext(ProjectConfig inst, ServiceRunner? self)
    {
        var ports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in inst.Services.Values)
        {
            var runner = Find(Ids.Service(inst.InstanceKey, s.Name));
            if (runner != null && (runner == self || runner.IsActive) && runner.Port != null) ports[s.Name] = runner.Port.Value;
            else if (inst.Worktree != null || s.AutoPort)
            {
                var sticky = _state.GetPort(inst.InstanceKey, s.Name);
                if (sticky != null) ports[s.Name] = sticky.Value;
                else if (inst.Worktree == null && s.Port != null) ports[s.Name] = s.Port.Value;
                else
                {
                    var taken = Runners.Where(x => x.Port != null).Select(x => x.Port!.Value).ToHashSet();
                    taken.UnionWith(_state.AllStickyPorts());
                    var p = _allocator.Allocate(taken);
                    if (p != null) { _state.SetPort(inst.InstanceKey, s.Name, p.Value); ports[s.Name] = p.Value; }
                }
            }
            else if (s.Port != null) ports[s.Name] = s.Port.Value;
        }
        return new Variables.Context { Root = inst.Root, Ports = ports };
    }

    private static Dictionary<string, string> BuildEnv(Dictionary<string, string> serviceEnv, Variables.Context ctx)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        env["FORCE_COLOR"] = "1";
        foreach (var (k, v) in serviceEnv) env[k] = Variables.Resolve(v, ctx);
        return env;
    }

    private static string? ResolveUrl(EffectiveService cfg, Variables.Context ctx, int? port)
    {
        if (cfg.Url != null)
        {
            var url = Variables.Resolve(cfg.Url, ctx);
            // A worktree copy runs on another port than the one written in a literal url.
            if (port != null && cfg.Port != null && port != cfg.Port && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Port == cfg.Port)
                url = new UriBuilder(u) { Port = port.Value }.Uri.ToString().TrimEnd('/');
            return url;
        }
        return port != null ? $"http://localhost:{port}" : null;
    }

    private async Task<string?> WaitReadyAsync(ServiceRunner r, ManagedProcess proc, Variables.Context ctx, CancellationToken token)
    {
        var cfg = r.Config;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(cfg.ReadyTimeoutSeconds);
        var health = cfg.Health != null ? Variables.Resolve(cfg.Health, ctx) : null;
        var aliveSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (proc.Exited.IsCompleted)
                return $"process exited with code {proc.Exited.Result} before it was ready";
            if (health != null)
            {
                if (await HealthOkAsync(health, token)) return null;
            }
            else if (r.Port != null)
            {
                if (PortInspector.IsListening(r.Port.Value)) return null;
            }
            else if (DateTime.UtcNow - aliveSince >= _options.AliveGrace) return null;
            try { await Task.WhenAny(proc.Exited, Task.Delay(250, token)); }
            catch (OperationCanceledException) { throw; }
        }
        return $"not ready after {cfg.ReadyTimeoutSeconds}s" + (health != null ? $" (health {health})" : r.Port != null ? $" (port {r.Port} not listening)" : "");
    }

    private async Task<bool> HealthOkAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            return (int)resp.StatusCode is >= 200 and < 300;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private async Task HealthLoopAsync(ServiceRunner r, ManagedProcess proc, string url, CancellationToken token)
    {
        var failures = 0;
        try
        {
            while (!token.IsCancellationRequested && r.Process == proc)
            {
                await Task.Delay(_options.HealthInterval, token);
                if (r.Process != proc || !r.IsUp) return;
                var ok = await HealthOkAsync(url, token);
                if (ok)
                {
                    failures = 0;
                    if (r.State == ServiceState.Unhealthy)
                    {
                        r.UnhealthySince = null;
                        r.UnhealthyNotified = false;
                        r.Log.Append(LogStream.Sys, "--- healthy again ---");
                        SetState(r, ServiceState.Running);
                    }
                    continue;
                }
                failures++;
                if (failures >= _options.HealthFailuresForUnhealthy && r.State == ServiceState.Running)
                {
                    r.UnhealthySince = DateTimeOffset.Now;
                    r.Log.Append(LogStream.Sys, $"--- unhealthy: {url} failed {failures} times ---");
                    SetState(r, ServiceState.Unhealthy);
                }
                if (r.State == ServiceState.Unhealthy && !r.UnhealthyNotified && r.UnhealthySince is { } since &&
                    DateTimeOffset.Now - since >= _options.UnhealthyNotifyAfter)
                {
                    r.UnhealthyNotified = true;
                    Notify(r, "Service unhealthy", $"{r.Id} has been unhealthy for {(int)(DateTimeOffset.Now - since).TotalSeconds}s", true);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WatchExitAsync(ServiceRunner r, ManagedProcess proc)
    {
        var code = await proc.Exited;
        if (r.Process != proc || r.StopRequested) return; // replaced, or a stop owns the cleanup
        // Let the rest of the tree go too, so the port is free for a restart.
        await proc.KillAsync(TimeSpan.FromSeconds(2));
        if (r.Process != proc) return;
        r.Process = null;
        proc.Dispose();
        r.LastExitCode = code;
        if (r.StopRequested) return; // stop path sets the state

        var wasUp = r.IsUp;
        r.Log.Append(LogStream.Sys, $"--- process exited with code {code} ---");
        if (!wasUp) return; // still starting: WaitReadyAsync reports the failure

        r.LastError = $"exited with code {code}";
        SetState(r, ServiceState.Crashed);
        r.RunCts?.Cancel();

        var now = DateTimeOffset.Now;
        r.RecentCrashes.Add(now);
        r.RecentCrashes.RemoveAll(t => now - t > _options.CrashWindow);

        if (!r.Config.AutoRestart || !_state.IsDesired(r.Id))
        {
            Notify(r, "Service crashed", $"{r.Id} exited with code {code}", true);
            return;
        }
        if (r.RecentCrashes.Count >= _options.CrashLimit)
        {
            r.LastError = $"crashed {r.RecentCrashes.Count} times in {(int)_options.CrashWindow.TotalSeconds}s, giving up";
            r.Log.Append(LogStream.Sys, "--- " + r.LastError + " ---");
            SetState(r, ServiceState.Failed);
            Notify(r, "Service failed", $"{r.Id} {r.LastError}", true);
            return;
        }
        var delay = _options.Backoff(r.RecentCrashes.Count - 1);
        r.Log.Append(LogStream.Sys, $"--- restarting in {delay.TotalSeconds:0.#}s (auto restart) ---");
        await Task.Delay(delay);
        if (r.State != ServiceState.Crashed || !_state.IsDesired(r.Id) || _disposed) return;
        r.RestartCount++;
        var res = await StartWithDepsAsync(r, new StartOptions(), [], default);
        if (!res.Ok) Notify(r, "Service failed", $"{r.Id} could not restart: {res.Error}", true);
    }

    private void Notify(ServiceRunner r, string title, string message, bool isError) =>
        Notified?.Invoke(new Notification(r.Instance.Project, title, message, isError));

    // ---- Stop ----

    public async Task<ActionResultDto> StopAsync(string id)
    {
        var r = Find(id);
        if (r == null) return ActionResultDto.Fail($"unknown service '{id}'");
        var dependents = Runners.Where(x => x.IsUp && x.InstanceKey == r.InstanceKey &&
                                            x.Config.DependsOn.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).Select(x => x.Id).ToList();
        var res = await StopRunnerAsync(r, clearDesired: true, reason: null);
        if (res.Ok && dependents.Count > 0)
            res = res with { Message = res.Message + $" (warning: still running and depending on it: {string.Join(", ", dependents)})" };
        return res;
    }

    public async Task<ActionResultDto> StopManyAsync(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        var results = await Task.WhenAll(list.Select(async id => (id, await StopRunnerAsync(Find(id), true, null))));
        return Combine([.. results], "stopped");
    }

    private async Task<ActionResultDto> StopRunnerAsync(ServiceRunner? r, bool clearDesired, string? reason)
    {
        if (r == null) return ActionResultDto.Fail("unknown service");
        if (clearDesired) _state.SetDesired(r.Id, false);
        r.StopRequested = true;
        r.RunCts?.Cancel(); // aborts a start in progress
        await r.Gate.WaitAsync();
        try
        {
            var proc = r.Process;
            if (proc == null)
            {
                if (r.State != ServiceState.Stopped) SetState(r, ServiceState.Stopped);
                return ActionResultDto.Success($"{r.Id} is not running", r.Id);
            }
            SetState(r, ServiceState.Stopping);
            r.Log.Append(LogStream.Sys, $"--- stopping {r.Id}{(reason != null ? " (" + reason + ")" : "")} ---");
            await proc.KillAsync();
            r.Process = null;
            r.LastExitCode = proc.Exited.IsCompleted ? proc.Exited.Result : null;
            proc.Dispose();
            // Windows drops a dead process's listening socket slightly after the process is gone.
            if (r.Port is int port)
            {
                var until = DateTime.UtcNow.AddSeconds(5);
                while (PortInspector.IsListening(port) && DateTime.UtcNow < until) await Task.Delay(50);
            }
            r.StartedAt = null;
            SetState(r, ServiceState.Stopped);
            r.Log.Append(LogStream.Sys, $"--- stopped ---");
            return ActionResultDto.Success($"{r.Id} stopped", r.Id);
        }
        finally
        {
            r.Gate.Release();
        }
    }

    public async Task<ActionResultDto> RestartAsync(string id, StartOptions? options = null)
    {
        var r = Find(id);
        if (r == null) return ActionResultDto.Fail($"unknown service '{id}'");
        await StopRunnerAsync(r, clearDesired: false, reason: "restart");
        r.RestartCount++;
        return await StartAsync(id, options);
    }

    public async Task<ActionResultDto> RestartManyAsync(IEnumerable<string> ids, StartOptions? options = null)
    {
        var list = ids.ToList();
        await Task.WhenAll(list.Select(id => StopRunnerAsync(Find(id), false, "restart")));
        foreach (var r in list.Select(Find).Where(r => r != null)) r!.RestartCount++;
        return await StartManyAsync(list, options);
    }

    /// <summary>Waits until the service is up or has failed.</summary>
    public async Task<ActionResultDto> WaitAsync(string id, TimeSpan timeout)
    {
        var r = Find(id);
        if (r == null) return ActionResultDto.Fail($"unknown service '{id}'");
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (r.IsUp) return ActionResultDto.Success($"{r.Id} running{(r.Url != null ? " at " + r.Url : "")}", r.Id);
            if (r.State is ServiceState.Failed or ServiceState.Crashed)
                return ActionResultDto.Fail($"{r.Id}: {r.State.ToString().ToLowerInvariant()}: {r.LastError}", r.Log.TailText(50));
            if (r.State == ServiceState.Stopped) return ActionResultDto.Fail($"{r.Id} is stopped");
            await Task.Delay(200);
        }
        return ActionResultDto.Fail($"{r.Id} still {r.State.ToString().ToLowerInvariant()} after {timeout.TotalSeconds:0}s", r.Log.TailText(50));
    }

    // ---- Restore / shutdown ----

    /// <summary>Starts every service that was running when the daemon last stopped.</summary>
    public async Task RestoreAsync()
    {
        var desired = _state.Desired.Where(id => Find(id) != null).ToList();
        if (desired.Count > 0) await StartManyAsync(desired);
    }

    /// <summary>Stops everything, keeping the desired state so the next start restores it.</summary>
    public async Task ShutdownAsync()
    {
        await Task.WhenAll(Runners.Where(r => r.Process != null).Select(r => StopRunnerAsync(r, false, "RepoManager shutting down")));
        foreach (var t in Tasks) { if (t.Process != null) await t.Process.KillAsync(); }
    }

    // ---- Tasks ----

    public async Task<ActionResultDto> RunTaskAsync(string id)
    {
        var t = FindTask(id);
        if (t == null) return ActionResultDto.Fail($"unknown task '{id}'");
        if (!t.Instance.Valid) return ActionResultDto.Fail($"config of {t.Instance.InstanceKey} has errors: {string.Join("; ", t.Instance.Errors)}");
        ManagedProcess proc;
        lock (t)
        {
            if (t.Process != null) return ActionResultDto.Fail($"task {t.Id} is already running");
            var ctx = VariableContext(t.Instance, null);
            var cwd = Path.GetFullPath(Path.Combine(t.Instance.Root, Variables.Resolve(t.Config.Cwd, ctx)));
            var cmd = ManagedProcess.BuildCommandLine(Variables.Resolve(t.Config.Command, ctx), t.Config.Args.Select(a => Variables.Resolve(a, ctx)));
            t.Log.Append(LogStream.Sys, $"--- running task {t.Id}: {cmd} ---");
            try
            {
                proc = ManagedProcess.Start(new ProcessSpec { CommandLine = cmd, WorkingDirectory = cwd, Environment = BuildEnv(t.Config.Env, ctx) }, t.Log.Append);
            }
            catch (Exception ex)
            {
                t.Log.Append(LogStream.Sys, "--- could not start: " + ex.Message + " ---");
                return ActionResultDto.Fail($"task {t.Id} could not start: {ex.Message}");
            }
            t.Process = proc;
            t.LastRunAt = DateTimeOffset.Now;
            t.LastExitCode = null;
        }
        TaskChanged?.Invoke(t.ToDto());
        _ = Task.Run(async () =>
        {
            var code = await proc.Exited;
            await proc.KillAsync(TimeSpan.FromSeconds(2));
            lock (t) { t.Process = null; t.LastExitCode = code; }
            proc.Dispose();
            t.Log.Append(LogStream.Sys, $"--- task exited with code {code} ---");
            TaskChanged?.Invoke(t.ToDto());
        });
        return ActionResultDto.Success($"task {t.Id} started", t.Id);
    }

    public async Task<ActionResultDto> WaitTaskAsync(string id, TimeSpan timeout)
    {
        var t = FindTask(id);
        if (t == null) return ActionResultDto.Fail($"unknown task '{id}'");
        var until = DateTime.UtcNow + timeout;
        while (t.Running && DateTime.UtcNow < until) await Task.Delay(200);
        if (t.Running) return ActionResultDto.Fail($"task {t.Id} still running after {timeout.TotalSeconds:0}s", t.Log.TailText(50));
        return t.LastExitCode == 0
            ? new ActionResultDto { Ok = true, Message = $"task {t.Id} exited with code 0", Affected = [t.Id], LogTail = t.Log.TailText(50) }
            : ActionResultDto.Fail($"task {t.Id} exited with code {t.LastExitCode}", t.Log.TailText(50));
    }

    public async Task<ActionResultDto> StopTaskAsync(string id)
    {
        var t = FindTask(id);
        if (t?.Process == null) return ActionResultDto.Fail($"task '{id}' is not running");
        await t.Process.KillAsync();
        return ActionResultDto.Success($"task {id} stopped", id);
    }

    // ---- Events ----

    private void SetState(ServiceRunner r, ServiceState state)
    {
        r.State = state;
        try { ServiceChanged?.Invoke(ToDto(r)); } catch { /* listeners must not break the supervisor */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _config.Changed -= Sync;
        foreach (var r in Runners) { r.RunCts?.Cancel(); r.Process?.Dispose(); }
        foreach (var t in Tasks) t.Process?.Dispose();
        _http.Dispose();
    }
}
