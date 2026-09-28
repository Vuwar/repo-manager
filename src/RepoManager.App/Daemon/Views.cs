using System.Diagnostics;
using System.Text.Json;
using RepoManager.Contracts;
using RepoManager.Core.Config;
using RepoManager.Core.Hooks;
using RepoManager.Core.Ports;
using RepoManager.Core.Processes;

namespace RepoManager.App.Daemon;

/// <summary>Builds API view models from core state.</summary>
public static class Views
{
    public static List<ProjectDto> Projects(DaemonHost d, bool withGit)
    {
        var sup = d.Supervisor;
        var instances = d.Config.Instances;
        var result = new List<ProjectDto>();
        foreach (var p in d.Config.Registry.Projects)
        {
            var name = p.EffectiveName;
            var mine = instances.Where(i => i.Project.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            var main = mine.FirstOrDefault(i => i.Worktree == null);
            var errors = (main?.Errors ?? []).ToList();
            var warnings = d.Config.WarningsFor(name);
            result.Add(new ProjectDto
            {
                Name = name,
                Root = p.Path,
                Tags = p.Tags,
                Notifications = p.Notifications,
                Valid = main?.Valid ?? false,
                Errors = [.. errors, .. warnings.Select(w => "warning: " + w)],
                Instances = mine.Select(i => new InstanceDto
                {
                    Key = i.InstanceKey,
                    Worktree = i.Worktree,
                    Root = i.Root,
                    Git = withGit && Directory.Exists(i.Root) ? d.Git.GetInfo(i.Root) : null,
                    Services = i.Services.Keys
                        .Select(n => sup.Find(Ids.Service(i.InstanceKey, n)))
                        .Where(r => r != null)
                        .Select(r => sup.ToDto(r!))
                        .ToList(),
                    Tasks = i.Tasks.Keys
                        .Select(n => sup.FindTask(Ids.Service(i.InstanceKey, n)))
                        .Where(t => t != null)
                        .Select(t => t!.ToDto())
                        .ToList(),
                }).ToList(),
            });
        }
        return result;
    }

    /// <summary>
    /// Listening ports. Without <paramref name="all"/>: only dev rows (managed, or your own non-Windows processes
    /// below the dynamic range, not hidden), plus any other owner of a port a dev row uses, so conflicts stay visible.
    /// </summary>
    public static List<PortDto> Ports(DaemonHost d, bool withCommandLines, bool all)
    {
        var listening = PortInspector.Listening();
        var rows = PortView.Build(listening, d.Supervisor.ManagedPids(), ProcessInfo.Get, PortInspector.ProcessName,
            new Dictionary<int, string>(), d.Config.Registry.HiddenPortProcesses, Environment.ProcessId);
        if (!all)
        {
            var devPorts = rows.Where(r => r.Dev).Select(r => r.Port).ToHashSet();
            rows = rows.Where(r => devPorts.Contains(r.Port)).ToList();
        }
        if (!withCommandLines) return rows;
        var cmds = PortInspector.CommandLines(rows.Select(r => r.Pid));
        return rows.Select(r => r with { CommandLine = cmds.GetValueOrDefault(r.Pid) }).ToList();
    }

    public static EffectiveConfigDto Config(DaemonHost d, ProjectConfig inst)
    {
        static ConfigFieldDto F(string field, object? value, Dictionary<string, string> origins) =>
            new(field, value switch
            {
                null => null,
                string s => s,
                bool b => b ? "true" : "false",
                System.Collections.IEnumerable e => JsonSerializer.Serialize(e, Protocol.Json),
                _ => value.ToString(),
            }, origins.GetValueOrDefault(field, Layers.Default));

        var project = d.Config.FindProject(inst.Project);
        return new EffectiveConfigDto
        {
            Project = inst.Project,
            Worktree = inst.Worktree,
            Errors = inst.Errors,
            OverridesJson = JsonSerializer.Serialize(project?.Overrides ?? new RepoConfigFile(), ConfigService.IndentedJson),
            Services = inst.Services.Values.Select(s => new EffectiveServiceDto(s.Name,
            [
                F("command", s.Command, s.Origins),
                F("args", s.Args, s.Origins),
                F("cwd", s.Cwd, s.Origins),
                F("env", s.Env, s.Origins),
                F("port", s.Port, s.Origins),
                F("autoPort", s.AutoPort, s.Origins),
                F("url", s.Url, s.Origins),
                F("health", s.Health, s.Origins),
                F("dependsOn", s.DependsOn, s.Origins),
                F("prepare", s.Prepare.Select(p => p.If == null ? p.Run : $"if {p.If}: {p.Run}"), s.Origins),
                F("autoRestart", s.AutoRestart, s.Origins),
                F("readyTimeoutSeconds", s.ReadyTimeoutSeconds, s.Origins),
                F("disabled", s.Disabled, s.Origins),
            ])).ToList(),
            Tasks = inst.Tasks.Values.Select(t => new EffectiveServiceDto(t.Name,
            [
                F("command", t.Command, t.Origins),
                F("args", t.Args, t.Origins),
                F("cwd", t.Cwd, t.Origins),
                F("env", t.Env, t.Origins),
            ])).ToList(),
        };
    }

    public static ActionResultDto Open(DaemonHost d, OpenRequest req)
    {
        var inst = d.Config.FindInstance(req.Instance);
        if (inst == null) return ActionResultDto.Fail($"unknown instance '{req.Instance}'");
        var path = inst.Root;
        if (!Directory.Exists(path)) return ActionResultDto.Fail($"folder not found: {path}");
        try
        {
            switch (req.Tool.ToLowerInvariant())
            {
                case "explorer":
                    Process.Start(new ProcessStartInfo("explorer.exe", Quote(path)) { UseShellExecute = true });
                    break;
                case "vscode":
                    if (!RunOnPath("code", path)) return ActionResultDto.Fail("VS Code ('code') not found on PATH");
                    break;
                case "rider":
                    if (!RunOnPath("rider64", path) && !RunOnPath("rider", path)) return ActionResultDto.Fail("Rider not found on PATH (enable shell scripts in JetBrains Toolbox)");
                    break;
                case "terminal":
                    if (FindOnPath("wt") != null)
                        Process.Start(new ProcessStartInfo("wt.exe", $"-d {Quote(path)}") { UseShellExecute = true });
                    else
                        Process.Start(new ProcessStartInfo("cmd.exe", $"/k cd /d {Quote(path)}") { UseShellExecute = true, WorkingDirectory = path });
                    break;
                default:
                    return ActionResultDto.Fail($"unknown tool '{req.Tool}' (explorer, vscode, rider, terminal)");
            }
            return ActionResultDto.Success($"opened {path} in {req.Tool}");
        }
        catch (Exception ex)
        {
            return ActionResultDto.Fail(ex.Message);
        }
    }

    private static string Quote(string p) => "\"" + p + "\"";

    private static bool RunOnPath(string exe, string path)
    {
        var full = FindOnPath(exe);
        if (full == null) return false;
        var psi = full.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("cmd.exe", $"/c \"{Quote(full)} {Quote(path)}\"") { CreateNoWindow = true, UseShellExecute = false }
            : new ProcessStartInfo(full, Quote(path)) { UseShellExecute = false };
        Process.Start(psi);
        return true;
    }

    public static string? FindOnPath(string exe)
    {
        var exts = new[] { ".exe", ".cmd", ".bat" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JetBrains", "Toolbox", "scripts"));
        foreach (var dir in dirs)
            foreach (var ext in exts)
            {
                try
                {
                    var p = Path.Combine(dir.Trim('"'), exe + ext);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        return null;
    }

    public static List<ManagedServiceInfo> HookSnapshot(Supervisor sup)
    {
        var names = new Dictionary<int, string>();
        var result = new List<ManagedServiceInfo>();
        foreach (var r in sup.Runners)
        {
            var pids = r.Process?.TreePids() ?? [];
            var procNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pid in pids)
            {
                if (!names.TryGetValue(pid, out var n))
                    names[pid] = n = PortInspector.ProcessName(pid)?.ToLowerInvariant() ?? "";
                if (n.Length > 0) procNames.Add(n);
            }
            string cwd;
            try { cwd = Path.GetFullPath(Path.Combine(r.Instance.Root, r.Config.Cwd.Contains("${") ? "." : r.Config.Cwd)); }
            catch { cwd = r.Instance.Root; }
            result.Add(new ManagedServiceInfo
            {
                Id = r.Id,
                Name = r.Name,
                InstanceKey = r.InstanceKey,
                Root = r.Instance.Root,
                Cwd = cwd,
                Up = r.IsUp,
                Active = r.IsActive,
                Port = r.IsActive ? r.Port : null,
                Pids = pids.ToHashSet(),
                ProcessNames = procNames,
                Command = r.Config.Command,
                Args = r.Config.Args,
                Url = r.Url,
            });
        }
        return result;
    }
}
