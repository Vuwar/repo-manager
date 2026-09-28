using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RepoManager.Contracts;
using RepoManager.Core.Config;
using RepoManager.Core.Hooks;
using RepoManager.Core.Ports;
using RepoManager.Core.Processes;

namespace RepoManager.App.Daemon;

/// <summary>HTTP API on 127.0.0.1. Every /api call needs the token (header, or ?token= for EventSource).</summary>
public static class Api
{
    public static void Map(WebApplication app, DaemonHost d)
    {
        app.Use(async (ctx, next) =>
        {
            // DNS rebinding guard: only loopback host names.
            var host = ctx.Request.Host.Host;
            if (host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
            {
                ctx.Response.StatusCode = 403;
                return;
            }
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                var token = ctx.Request.Headers[Protocol.TokenHeader].FirstOrDefault() ?? ctx.Request.Query["token"].FirstOrDefault();
                if (!string.Equals(token, d.Token, StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsJsonAsync(ActionResultDto.Fail("missing or wrong token"));
                    return;
                }
            }
            await next();
        });

        var api = app.MapGroup("/api");
        var sup = d.Supervisor;

        api.MapGet("/ping", () => new DaemonInfoDto(Protocol.Version, Environment.ProcessId, d.Port, d.StartedAt));

        api.MapGet("/projects", (bool? git) => Views.Projects(d, git ?? true));

        api.MapGet("/services", () => sup.Runners.Select(sup.ToDto).ToList());

        api.MapGet("/resolve", (string? target, string? cwd, bool? tasks) => sup.Resolve(target, cwd, tasks ?? false));

        api.MapPost("/services/start", async (ServiceActionRequest req) =>
        {
            var r = sup.Resolve(req.Target, req.Cwd);
            if (r.Error != null) return ActionResultDto.Fail(r.Error);
            var opts = new StartOptions(req.KillOwner, req.Force);
            return r.ServiceIds.Count == 1 ? await sup.StartAsync(r.ServiceIds[0], opts) : await sup.StartManyAsync(r.ServiceIds, opts);
        });

        api.MapPost("/services/stop", async (ServiceActionRequest req) =>
        {
            var r = sup.Resolve(req.Target, req.Cwd);
            if (r.Error != null) return ActionResultDto.Fail(r.Error);
            return r.ServiceIds.Count == 1 ? await sup.StopAsync(r.ServiceIds[0]) : await sup.StopManyAsync(r.ServiceIds);
        });

        api.MapPost("/services/restart", async (ServiceActionRequest req) =>
        {
            var r = sup.Resolve(req.Target, req.Cwd);
            if (r.Error != null) return ActionResultDto.Fail(r.Error);
            var opts = new StartOptions(req.KillOwner, req.Force);
            return r.ServiceIds.Count == 1 ? await sup.RestartAsync(r.ServiceIds[0], opts) : await sup.RestartManyAsync(r.ServiceIds, opts);
        });

        api.MapGet("/services/wait", async (string target, string? cwd, int? timeout) =>
        {
            var r = sup.Resolve(target, cwd);
            if (r.Error != null) return ActionResultDto.Fail(r.Error);
            var results = await Task.WhenAll(r.ServiceIds.Select(id => sup.WaitAsync(id, TimeSpan.FromSeconds(timeout ?? 120))));
            var failed = results.FirstOrDefault(x => !x.Ok);
            return failed ?? new ActionResultDto { Ok = true, Message = string.Join("\n", results.Select(x => x.Message)), Affected = r.ServiceIds };
        });

        api.MapGet("/logs", (string? target, string? cwd, int? tail, string? since, string? grep, bool? task) =>
        {
            var isTask = task ?? false;
            IReadOnlyList<string> sources;
            if (target != null && target.StartsWith("task:", StringComparison.OrdinalIgnoreCase))
                sources = [target];
            else
            {
                var r = sup.Resolve(target, cwd, isTask);
                if (r.Error != null) return Results.BadRequest(ActionResultDto.Fail(r.Error));
                sources = isTask ? r.ServiceIds.Select(i => "task:" + i).ToList() : r.ServiceIds;
            }
            DateTimeOffset? from = null;
            if (since != null)
            {
                var span = ParseDuration(since);
                if (span != null) from = DateTimeOffset.Now - span.Value;
                else if (DateTimeOffset.TryParse(since, out var ts)) from = ts;
                else return Results.BadRequest(ActionResultDto.Fail($"bad since '{since}' (use 30s, 5m, 1h or a timestamp)"));
            }
            var lines = sources
                .SelectMany(s => d.Logs.TryGet(s, out var b) ? b!.Query(null, from, grep) : [])
                .OrderBy(l => l.Timestamp)
                .ToList();
            var n = tail ?? 200;
            if (lines.Count > n) lines = lines.GetRange(lines.Count - n, n);
            return Results.Ok(lines);
        });

        api.MapPost("/instances/start", async (InstanceActionRequest req) =>
        {
            var inst = d.Config.FindInstance(req.Instance);
            if (inst == null) return ActionResultDto.Fail($"unknown instance '{req.Instance}'");
            return await sup.StartManyAsync(inst.Services.Values.Where(s => !s.Disabled).Select(s => Ids.Service(inst.InstanceKey, s.Name)));
        });

        api.MapPost("/instances/stop", async (InstanceActionRequest req) =>
        {
            var inst = d.Config.FindInstance(req.Instance);
            if (inst == null) return ActionResultDto.Fail($"unknown instance '{req.Instance}'");
            return await sup.StopManyAsync(inst.Services.Keys.Select(n => Ids.Service(inst.InstanceKey, n)));
        });

        api.MapPost("/groups/start", async (GroupActionRequest req) =>
        {
            var ids = GroupServices(d, req.Tag).ToList();
            return ids.Count == 0 ? ActionResultDto.Fail($"no services in group '{req.Tag}'") : await sup.StartManyAsync(ids);
        });

        api.MapPost("/groups/stop", async (GroupActionRequest req) =>
        {
            var ids = GroupServices(d, req.Tag, includeDisabled: true).ToList();
            return ids.Count == 0 ? ActionResultDto.Fail($"no services in group '{req.Tag}'") : await sup.StopManyAsync(ids);
        });

        api.MapPost("/tasks/run", async (TaskRunRequest req, bool? wait, int? timeout) =>
        {
            var r = sup.Resolve(req.Target, req.Cwd, tasks: true);
            if (r.Error != null) return ActionResultDto.Fail(r.Error);
            if (r.ServiceIds.Count != 1) return ActionResultDto.Fail("name one task");
            var id = r.ServiceIds[0];
            var started = await sup.RunTaskAsync(id);
            if (!started.Ok || wait != true) return started;
            return await sup.WaitTaskAsync(id, TimeSpan.FromSeconds(timeout ?? 600));
        });

        api.MapPost("/tasks/stop", async (TaskRunRequest req) =>
        {
            var r = sup.Resolve(req.Target, req.Cwd, tasks: true);
            if (r.Error != null) return ActionResultDto.Fail(r.Error);
            return await sup.StopTaskAsync(r.ServiceIds[0]);
        });

        api.MapGet("/events", async (HttpContext ctx, string? logs) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var sub = d.Events.Subscribe((logs ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            try
            {
                await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                var reader = sub.Channel.Reader;
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    var readTask = reader.WaitToReadAsync(ctx.RequestAborted).AsTask();
                    var done = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(15), ctx.RequestAborted));
                    if (done != readTask)
                    {
                        await ctx.Response.WriteAsync(": ping\n\n", ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        await readTask; // keep a single outstanding wait
                    }
                    if (!await readTask) break;
                    while (reader.TryRead(out var e))
                        await ctx.Response.WriteAsync("data: " + JsonSerializer.Serialize(e, Protocol.Json) + "\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                d.Events.Unsubscribe(sub);
            }
        });

        api.MapGet("/ports", (bool? cmd) => Views.Ports(sup, cmd ?? false));

        api.MapPost("/ports/kill", (KillPidRequest req) =>
        {
            var managed = sup.ManagedPids();
            if (managed.TryGetValue(req.Pid, out var owner))
                return ActionResultDto.Fail($"PID {req.Pid} belongs to {owner}; stop the service instead");
            if (req.Pid == Environment.ProcessId) return ActionResultDto.Fail("that is RepoManager itself");
            var name = PortInspector.ProcessName(req.Pid);
            if (name == null) return ActionResultDto.Fail($"no process {req.Pid}");
            PortInspector.KillTree(req.Pid);
            return ActionResultDto.Success($"killed {req.Pid} ({name})");
        });

        api.MapPost("/projects/add", (AddProjectRequest req) => d.Config.AddProject(req.Path));
        api.MapPost("/projects/remove", async (RemoveProjectRequest req) =>
        {
            var ids = sup.Runners.Where(r => r.Instance.Project.Equals(req.Project, StringComparison.OrdinalIgnoreCase)).Select(r => r.Id).ToList();
            if (ids.Count > 0) await sup.StopManyAsync(ids);
            return d.Config.RemoveProject(req.Project);
        });
        api.MapPost("/projects/settings", (ProjectSettingsRequest req) => d.Config.UpdateProject(req));

        api.MapGet("/scan", (string dir) => d.Config.Scan(dir));

        api.MapGet("/config", (string instance) =>
        {
            var inst = d.Config.FindInstance(instance);
            return inst == null ? Results.NotFound(ActionResultDto.Fail($"unknown instance '{instance}'")) : Results.Ok(Views.Config(d, inst));
        });

        api.MapPost("/open", (OpenRequest req) => Views.Open(d, req));

        api.MapPost("/hook", (HookRequest req) => HookPolicy.Evaluate(req, Views.HookSnapshot(sup)));

        api.MapPost("/refresh", () =>
        {
            d.Config.Reload();
            return ActionResultDto.Success("reloaded");
        });

        api.MapPost("/app/activate", () =>
        {
            d.RequestActivate();
            return ActionResultDto.Success("activated");
        });

        api.MapPost("/app/quit", () =>
        {
            _ = Task.Delay(200).ContinueWith(_ => d.RequestQuit());
            return ActionResultDto.Success("quitting");
        });

        // Embedded React UI.
        Ui.Map(app);
    }

    private static IEnumerable<string> GroupServices(DaemonHost d, string tag, bool includeDisabled = false)
    {
        var projects = d.Config.Registry.Projects.Where(p => p.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).Select(p => p.EffectiveName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return d.Config.Instances
            .Where(i => i.Worktree == null && projects.Contains(i.Project))
            .SelectMany(i => i.Services.Values.Where(s => includeDisabled || !s.Disabled).Select(s => Ids.Service(i.InstanceKey, s.Name)));
    }

    public static TimeSpan? ParseDuration(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s.Length < 2) return null;
        if (!double.TryParse(s[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)) return null;
        return s[^1] switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => null,
        };
    }
}
