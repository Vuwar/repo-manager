using System.Text.Json;
using RepoManager.Contracts;
using RepoManager.Core.Git;

namespace RepoManager.Core.Config;

/// <summary>
/// Owns registry.json and the merged config of every checkout (main + worktrees) of every registered project.
/// Watches the config files and raises <see cref="Changed"/> after a reload.
/// </summary>
public sealed class ConfigService : IDisposable
{
    private readonly string _registryPath;
    private readonly GitService _git;
    private readonly object _lock = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _debounce;
    private readonly Timer _worktreePoll;
    private RegistryFile _registry = new();
    private List<ProjectConfig> _instances = [];
    private Dictionary<string, List<string>> _warnings = new(StringComparer.OrdinalIgnoreCase);
    private string _worktreeSignature = "";
    private bool _disposed;

    public event Action? Changed;

    public ConfigService(string dataDir, GitService git, bool watch = true)
    {
        Directory.CreateDirectory(dataDir);
        _registryPath = Path.Combine(dataDir, "registry.json");
        _git = git;
        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _worktreePoll = new Timer(_ => PollWorktrees(), null, Timeout.Infinite, Timeout.Infinite);
        Reload();
        if (watch)
        {
            WatchFile(dataDir, "registry.json");
            RefreshWatchers();
            _worktreePoll.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    public RegistryFile Registry { get { lock (_lock) return _registry; } }

    public IReadOnlyList<ProjectConfig> Instances { get { lock (_lock) return _instances; } }

    public IReadOnlyList<string> WarningsFor(string project)
    {
        lock (_lock) return _warnings.TryGetValue(project, out var w) ? w : [];
    }

    public ProjectConfig? FindInstance(string instanceKey)
    {
        lock (_lock) return _instances.FirstOrDefault(i => string.Equals(i.InstanceKey, instanceKey, StringComparison.OrdinalIgnoreCase));
    }

    public RegistryProject? FindProject(string name)
    {
        lock (_lock) return _registry.Projects.FirstOrDefault(p => string.Equals(p.EffectiveName, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Instance whose root contains <paramref name="cwd"/>; the deepest match wins (worktrees nested in the repo).</summary>
    public ProjectConfig? InstanceForPath(string cwd)
    {
        var full = GitService.Normalize(cwd) + "\\";
        lock (_lock)
            return _instances
                .Where(i => full.StartsWith(GitService.Normalize(i.Root) + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.Root.Length)
                .FirstOrDefault();
    }

    public void Reload()
    {
        if (_disposed) return;
        var errors = new List<string>();
        var registry = ConfigLoader.ReadJson<RegistryFile>(_registryPath, errors) ?? new RegistryFile();

        var instances = new List<ProjectConfig>();
        var warnings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var regErrors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var sig = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in registry.Projects)
        {
            var name = p.EffectiveName;
            if (!seenNames.Add(name))
            {
                regErrors.GetOrAdd(name).Add($"duplicate project name '{name}' ({p.Path}); set a different name");
                continue;
            }
            if (!Directory.Exists(p.Path))
            {
                var missing = new ProjectConfig { Project = name, Root = p.Path };
                missing.Errors.Add($"folder not found: {p.Path}");
                instances.Add(missing);
                continue;
            }
            var main = ConfigLoader.Load(name, GitService.Normalize(p.Path), null, p.Overrides);
            main.Errors.InsertRange(0, errors.Select(e => "registry.json: " + e));
            instances.Add(main);

            foreach (var wt in _git.ListWorktrees(p.Path))
            {
                instances.Add(ConfigLoader.Load(name, wt.Path, wt.Name, p.Overrides));
                sig.Add(name + "|" + wt.Path);
            }
        }

        // Cross-project check: the same fixed port in two main checkouts. Reported as a warning, not an error:
        // old/new copies of one app often share a port and are never run together. The start-time port check
        // still stops a real clash.
        var fixedPorts = instances
            .Where(i => i.Worktree == null)
            .SelectMany(i => i.Services.Values.Where(s => s.Port != null && !s.AutoPort && !s.Disabled).Select(s => (Instance: i, Service: s)))
            .GroupBy(x => x.Service.Port!.Value);
        foreach (var g in fixedPorts.Where(g => g.Count() > 1))
        {
            var owners = string.Join(", ", g.Select(x => Ids.Service(x.Instance.InstanceKey, x.Service.Name)));
            foreach (var x in g) warnings.GetOrAdd(x.Instance.Project).Add($"port {g.Key} is used by several services: {owners}");
        }

        foreach (var (project, errs) in regErrors)
        {
            var inst = instances.FirstOrDefault(i => i.Project.Equals(project, StringComparison.OrdinalIgnoreCase) && i.Worktree == null);
            inst?.Errors.AddRange(errs);
        }

        lock (_lock)
        {
            _registry = registry;
            _instances = instances;
            _warnings = warnings;
            _worktreeSignature = string.Join(";", sig);
        }
        RefreshWatchers();
        Changed?.Invoke();
    }

    private void PollWorktrees()
    {
        var sig = new List<string>();
        foreach (var p in Registry.Projects.Where(p => Directory.Exists(p.Path)))
            foreach (var wt in _git.ListWorktrees(p.Path))
                sig.Add(p.EffectiveName + "|" + wt.Path);
        string current;
        lock (_lock) current = _worktreeSignature;
        if (string.Join(";", sig) != current) Reload();
    }

    /// <summary>Rescans worktrees now (UI open, CLI in a new worktree).</summary>
    public void RefreshWorktrees() => PollWorktrees();

    // ---- Registry mutations ----

    public ActionResultDto AddProject(string path)
    {
        if (!Directory.Exists(path)) return ActionResultDto.Fail($"folder not found: {path}");
        var full = GitService.Normalize(path);
        string name;
        lock (_lock)
        {
            if (_registry.Projects.Any(p => GitService.SamePath(p.Path, full)))
                return ActionResultDto.Fail($"already registered: {full}");
            name = Path.GetFileName(full);
            var baseName = name; var i = 2;
            while (_registry.Projects.Any(p => string.Equals(p.EffectiveName, name, StringComparison.OrdinalIgnoreCase))) name = $"{baseName}-{i++}";
            _registry.Projects.Add(new RegistryProject { Path = full, Name = name == baseName ? null : name });
            Save();
        }
        Reload();
        return ActionResultDto.Success($"added {name}", name);
    }

    public ActionResultDto RemoveProject(string name)
    {
        lock (_lock)
        {
            var p = _registry.Projects.FirstOrDefault(x => string.Equals(x.EffectiveName, name, StringComparison.OrdinalIgnoreCase));
            if (p == null) return ActionResultDto.Fail($"unknown project '{name}'");
            _registry.Projects.Remove(p);
            Save();
        }
        Reload();
        return ActionResultDto.Success($"removed {name}", name);
    }

    public ActionResultDto UpdateProject(ProjectSettingsRequest req)
    {
        lock (_lock)
        {
            var p = _registry.Projects.FirstOrDefault(x => string.Equals(x.EffectiveName, req.Project, StringComparison.OrdinalIgnoreCase));
            if (p == null) return ActionResultDto.Fail($"unknown project '{req.Project}'");
            if (req.Tags != null) p.Tags = req.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (req.Notifications != null) p.Notifications = req.Notifications.Value;
            if (req.OverridesJson != null)
            {
                try
                {
                    p.Overrides = string.IsNullOrWhiteSpace(req.OverridesJson)
                        ? new RepoConfigFile()
                        : JsonSerializer.Deserialize<RepoConfigFile>(req.OverridesJson, Protocol.Json) ?? new RepoConfigFile();
                }
                catch (JsonException ex)
                {
                    return ActionResultDto.Fail("overrides: " + ex.Message);
                }
            }
            Save();
        }
        Reload();
        return ActionResultDto.Success($"updated {req.Project}", req.Project);
    }

    public IReadOnlyList<ScanResultDto> Scan(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        var registry = Registry;
        var result = new List<ScanResultDto>();
        IEnumerable<string> subdirs;
        try { subdirs = Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return []; }
        foreach (var d in subdirs)
        {
            var markers = new List<string>();
            if (File.Exists(Path.Combine(d, ConfigLoader.LaunchJsonRelative))) markers.Add("launch.json");
            if (File.Exists(Path.Combine(d, ConfigLoader.DevServersFile))) markers.Add("devservers.json");
            if (HasFile(d, "package.json")) markers.Add("package.json");
            if (HasFile(d, "*.csproj")) markers.Add("csproj");
            if (Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git"))) markers.Add("git");
            if (markers.Count == 0 || markers.SequenceEqual(["git"])) continue;
            result.Add(new ScanResultDto(GitService.Normalize(d), Path.GetFileName(d),
                registry.Projects.Any(p => GitService.SamePath(p.Path, d)), markers));
        }
        lock (_lock)
        {
            if (!_registry.ScanRoots.Any(r => GitService.SamePath(r, dir)))
            {
                _registry.ScanRoots.Add(GitService.Normalize(dir));
                Save();
            }
        }
        return result;
    }

    /// <summary>Looks two levels deep, skipping dependency and build folders.</summary>
    private static bool HasFile(string dir, string pattern)
    {
        try
        {
            if (Directory.EnumerateFiles(dir, pattern).Any()) return true;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var n = Path.GetFileName(sub);
                if (n is "node_modules" or "bin" or "obj" or ".git" or ".next" or "dist") continue;
                if (Directory.EnumerateFiles(sub, pattern).Any()) return true;
            }
        }
        catch { /* unreadable folder */ }
        return false;
    }

    private void Save()
    {
        var tmp = _registryPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_registry, IndentedJson));
        File.Move(tmp, _registryPath, true);
    }

    public static readonly JsonSerializerOptions IndentedJson = new(Protocol.Json) { WriteIndented = true };

    // ---- File watching ----

    private void RefreshWatchers()
    {
        lock (_watchers)
        {
            // Keep the registry watcher (index 0 when watching), rebuild the rest.
            var keep = _watchers.Take(Math.Min(1, _watchers.Count)).ToList();
            foreach (var w in _watchers.Skip(keep.Count)) w.Dispose();
            _watchers.Clear();
            _watchers.AddRange(keep);
            if (keep.Count == 0) return;
            foreach (var inst in Instances)
            {
                if (!Directory.Exists(inst.Root)) continue;
                AddWatcher(inst.Root, ConfigLoader.DevServersFile, false);
                var claude = Path.Combine(inst.Root, ".claude");
                AddWatcher(Directory.Exists(claude) ? claude : inst.Root, Directory.Exists(claude) ? "launch.json" : ".claude", false);
            }
        }
    }

    private void WatchFile(string dir, string file)
    {
        lock (_watchers) AddWatcher(dir, file, true);
    }

    private void AddWatcher(string dir, string filter, bool first)
    {
        try
        {
            var w = new FileSystemWatcher(dir, filter)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler h = (_, _) => _debounce.Change(300, Timeout.Infinite);
            w.Changed += h; w.Created += h; w.Deleted += h;
            w.Renamed += (_, _) => _debounce.Change(300, Timeout.Infinite);
            w.EnableRaisingEvents = true;
            if (first) _watchers.Insert(0, w); else _watchers.Add(w);
        }
        catch { /* folder vanished or not watchable; next reload retries */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _debounce.Dispose();
        _worktreePoll.Dispose();
        lock (_watchers)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }
    }
}

internal static class DictionaryExtensions
{
    public static List<string> GetOrAdd(this Dictionary<string, List<string>> d, string key)
    {
        if (!d.TryGetValue(key, out var l)) d[key] = l = [];
        return l;
    }
}
