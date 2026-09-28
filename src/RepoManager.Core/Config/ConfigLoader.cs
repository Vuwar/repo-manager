using System.Text.Json;
using RepoManager.Contracts;

namespace RepoManager.Core.Config;

/// <summary>Reads the three layers for one checkout and merges them into a <see cref="ProjectConfig"/>.</summary>
public static class ConfigLoader
{
    public const string LaunchJsonRelative = ".claude\\launch.json";
    public const string DevServersFile = "devservers.json";

    public static ProjectConfig Load(string project, string root, string? worktree, RepoConfigFile? central)
    {
        var errors = new List<string>();
        var launch = ReadJson<LaunchJsonFile>(Path.Combine(root, LaunchJsonRelative), errors);
        var dev = ReadJson<RepoConfigFile>(Path.Combine(root, DevServersFile), errors);
        var cfg = Merge(project, root, worktree, launch, dev, central);
        cfg.Errors.InsertRange(0, errors);
        Validate(cfg);
        return cfg;
    }

    public static T? ReadJson<T>(string path, List<string> errors) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<T>(text, Protocol.Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    public static ProjectConfig Merge(string project, string root, string? worktree,
        LaunchJsonFile? launch, RepoConfigFile? dev, RepoConfigFile? central)
    {
        var cfg = new ProjectConfig { Project = project, Root = root, Worktree = worktree };

        // Layer 1 converted to the common shape.
        var layer1 = new Dictionary<string, ServiceConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in launch?.Configurations ?? [])
        {
            if (string.IsNullOrWhiteSpace(c.Name)) continue;
            layer1[c.Name] = new ServiceConfig
            {
                Command = c.RuntimeExecutable,
                Args = c.RuntimeArgs,
                Port = c.Port,
                AutoPort = c.AutoPort,
                Url = c.Url,
                Cwd = c.Cwd,
            };
        }

        var layers = new (string Name, Dictionary<string, ServiceConfig> Services)[]
        {
            (Layers.LaunchJson, layer1),
            (Layers.DevServers, dev?.Services ?? new(StringComparer.OrdinalIgnoreCase)),
            (Layers.Central, central?.Services ?? new(StringComparer.OrdinalIgnoreCase)),
        };

        var names = new List<string>();
        foreach (var (_, services) in layers)
            foreach (var n in services.Keys)
                if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);

        foreach (var name in names)
        {
            var e = new EffectiveService { Name = name };
            foreach (var key in new[] { "command", "args", "cwd", "env", "port", "autoPort", "url", "health", "dependsOn", "prepare", "autoRestart", "readyTimeoutSeconds", "disabled" })
                e.Origins[key] = Layers.Default;

            foreach (var (layer, services) in layers)
            {
                if (!services.TryGetValue(name, out var s) || s == null) continue;
                if (s.Command != null) { e.Command = s.Command; e.Origins["command"] = layer; }
                if (s.Args != null) { e.Args = [.. s.Args]; e.Origins["args"] = layer; }
                if (s.Cwd != null) { e.Cwd = s.Cwd; e.Origins["cwd"] = layer; }
                if (s.Env != null)
                {
                    foreach (var (k, v) in s.Env) e.Env[k] = v;
                    e.Origins["env"] = e.Origins["env"] == Layers.Default ? layer : e.Origins["env"] + "+" + layer;
                }
                if (s.Port != null) { e.Port = s.Port; e.Origins["port"] = layer; }
                if (s.AutoPort != null) { e.AutoPort = s.AutoPort.Value; e.Origins["autoPort"] = layer; }
                if (s.Url != null) { e.Url = s.Url; e.Origins["url"] = layer; }
                if (s.Health != null) { e.Health = s.Health; e.Origins["health"] = layer; }
                if (s.DependsOn != null) { e.DependsOn = [.. s.DependsOn]; e.Origins["dependsOn"] = layer; }
                if (s.Prepare != null) { e.Prepare = [.. s.Prepare]; e.Origins["prepare"] = layer; }
                if (s.AutoRestart != null) { e.AutoRestart = s.AutoRestart.Value; e.Origins["autoRestart"] = layer; }
                if (s.ReadyTimeoutSeconds != null) { e.ReadyTimeoutSeconds = s.ReadyTimeoutSeconds.Value; e.Origins["readyTimeoutSeconds"] = layer; }
                if (s.Disabled != null) { e.Disabled = s.Disabled.Value; e.Origins["disabled"] = layer; }
            }
            cfg.Services[name] = e;
        }

        var taskLayers = new (string Name, Dictionary<string, TaskConfig> Tasks)[]
        {
            (Layers.DevServers, dev?.Tasks ?? new(StringComparer.OrdinalIgnoreCase)),
            (Layers.Central, central?.Tasks ?? new(StringComparer.OrdinalIgnoreCase)),
        };
        foreach (var (layer, tasks) in taskLayers)
        {
            foreach (var (name, t) in tasks)
            {
                if (t == null) continue;
                if (!cfg.Tasks.TryGetValue(name, out var e))
                {
                    e = new EffectiveTask { Name = name };
                    foreach (var key in new[] { "command", "args", "cwd", "env" }) e.Origins[key] = Layers.Default;
                    cfg.Tasks[name] = e;
                }
                if (t.Command != null) { e.Command = t.Command; e.Origins["command"] = layer; }
                if (t.Args != null) { e.Args = [.. t.Args]; e.Origins["args"] = layer; }
                if (t.Cwd != null) { e.Cwd = t.Cwd; e.Origins["cwd"] = layer; }
                if (t.Env != null) { foreach (var (k, v) in t.Env) e.Env[k] = v; e.Origins["env"] = layer; }
            }
        }

        return cfg;
    }

    /// <summary>Checks one project in isolation. Cross-project checks live in ConfigService.</summary>
    public static void Validate(ProjectConfig cfg)
    {
        foreach (var s in cfg.Services.Values)
        {
            if (string.IsNullOrWhiteSpace(s.Command))
                cfg.Errors.Add($"service '{s.Name}' has no command");

            foreach (var dep in s.DependsOn)
            {
                if (!cfg.Services.ContainsKey(dep))
                    cfg.Errors.Add($"service '{s.Name}' depends on unknown service '{dep}'");
                else if (string.Equals(dep, s.Name, StringComparison.OrdinalIgnoreCase))
                    cfg.Errors.Add($"service '{s.Name}' depends on itself");
            }

            if (s.Port is < 1 or > 65535)
                cfg.Errors.Add($"service '{s.Name}' has invalid port {s.Port}");
            if (s.ReadyTimeoutSeconds < 1)
                cfg.Errors.Add($"service '{s.Name}' has invalid readyTimeoutSeconds {s.ReadyTimeoutSeconds}");

            foreach (var p in s.Prepare)
            {
                if (string.IsNullOrWhiteSpace(p.Run))
                    cfg.Errors.Add($"service '{s.Name}' has a prepare step without 'run'");
                if (p.If != null && ParseCondition(p.If) == null)
                    cfg.Errors.Add($"service '{s.Name}' has an invalid prepare condition '{p.If}' (use exists(path) or !exists(path))");
            }

            foreach (var value in Variables.AllValues(s))
                foreach (var r in Variables.Find(value))
                    CheckReference(cfg, s.Name, r);
        }

        foreach (var t in cfg.Tasks.Values)
        {
            if (string.IsNullOrWhiteSpace(t.Command))
                cfg.Errors.Add($"task '{t.Name}' has no command");
            foreach (var value in t.Args.Append(t.Command).Append(t.Cwd).Concat(t.Env.Values))
                foreach (var r in Variables.Find(value))
                    CheckReference(cfg, "task " + t.Name, r);
        }

        var cycle = FindCycle(cfg);
        if (cycle != null)
            cfg.Errors.Add("dependency cycle: " + string.Join(" -> ", cycle));
    }

    private static void CheckReference(ProjectConfig cfg, string owner, Variables.Reference r)
    {
        switch (r.Kind)
        {
            case "root":
                break;
            case "env":
                if (string.IsNullOrEmpty(r.Arg)) cfg.Errors.Add($"'{owner}': {r.Raw} needs a variable name");
                break;
            case "port":
                if (string.IsNullOrEmpty(r.Arg) || !cfg.Services.TryGetValue(r.Arg, out var target))
                    cfg.Errors.Add($"'{owner}': {r.Raw} references an unknown service");
                else if (target.Port == null && !target.AutoPort)
                    cfg.Errors.Add($"'{owner}': {r.Raw} references service '{target.Name}', which has no port and no autoPort");
                break;
            default:
                cfg.Errors.Add($"'{owner}': unknown variable {r.Raw}");
                break;
        }
    }

    public static List<string>? FindCycle(ProjectConfig cfg)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 1 visiting, 2 done
        var stack = new List<string>();

        List<string>? Visit(string n)
        {
            if (state.TryGetValue(n, out var st))
            {
                if (st == 2) return null;
                var i = stack.FindIndex(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
                return [.. stack.Skip(i), n];
            }
            state[n] = 1;
            stack.Add(n);
            if (cfg.Services.TryGetValue(n, out var s))
                foreach (var d in s.DependsOn)
                    if (cfg.Services.ContainsKey(d) && !string.Equals(d, n, StringComparison.OrdinalIgnoreCase))
                    {
                        var c = Visit(d);
                        if (c != null) return c;
                    }
            stack.RemoveAt(stack.Count - 1);
            state[n] = 2;
            return null;
        }

        foreach (var n in cfg.Services.Keys)
        {
            var c = Visit(n);
            if (c != null) return c;
        }
        return null;
    }

    /// <summary>Services in start order: dependencies first. Only the given targets and their dependencies.</summary>
    public static List<string> StartOrder(ProjectConfig cfg, IEnumerable<string> targets)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string n)
        {
            if (!seen.Add(n) || !cfg.Services.TryGetValue(n, out var s)) return;
            foreach (var d in s.DependsOn) Add(d);
            result.Add(s.Name);
        }
        foreach (var t in targets) Add(t);
        return result;
    }

    /// <summary>Parses "exists(path)" / "!exists(path)". Returns (negate, path) or null.</summary>
    public static (bool Negate, string Path)? ParseCondition(string condition)
    {
        var c = condition.Trim();
        var negate = c.StartsWith('!');
        if (negate) c = c[1..].TrimStart();
        if (!c.StartsWith("exists(", StringComparison.OrdinalIgnoreCase) || !c.EndsWith(')')) return null;
        var path = c["exists(".Length..^1].Trim().Trim('"', '\'');
        return path.Length == 0 ? null : (negate, path);
    }
}
