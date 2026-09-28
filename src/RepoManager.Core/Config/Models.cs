using System.Text.Json.Serialization;

namespace RepoManager.Core.Config;

/// <summary>One service as written in a config layer. Every field is optional so layers can be merged.</summary>
public sealed class ServiceConfig
{
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
    public string? Cwd { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public int? Port { get; set; }
    public bool? AutoPort { get; set; }
    public string? Url { get; set; }
    public string? Health { get; set; }
    public List<string>? DependsOn { get; set; }
    public List<PrepareStep>? Prepare { get; set; }
    public bool? AutoRestart { get; set; }
    public int? ReadyTimeoutSeconds { get; set; }
    public bool? Disabled { get; set; }
}

public sealed class PrepareStep
{
    /// <summary>Optional condition: "exists(path)" or "!exists(path)", path relative to the service cwd.</summary>
    public string? If { get; set; }
    public string Run { get; set; } = "";
}

public sealed class TaskConfig
{
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
    public string? Cwd { get; set; }
    public Dictionary<string, string>? Env { get; set; }
}

/// <summary>Shape of devservers.json (layer 2) and of the per-project overrides in registry.json (layer 3).</summary>
public sealed class RepoConfigFile
{
    public Dictionary<string, ServiceConfig> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TaskConfig> Tasks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Shape of .claude/launch.json (layer 1). Only the fields RepoManager uses.</summary>
public sealed class LaunchJsonFile
{
    public List<LaunchConfiguration> Configurations { get; set; } = [];
}

public sealed class LaunchConfiguration
{
    public string? Name { get; set; }
    public string? RuntimeExecutable { get; set; }
    public List<string>? RuntimeArgs { get; set; }
    public int? Port { get; set; }
    public bool? AutoPort { get; set; }
    public string? Url { get; set; }
    public string? Cwd { get; set; }
}

/// <summary>registry.json: the central layer.</summary>
public sealed class RegistryFile
{
    public List<RegistryProject> Projects { get; set; } = [];
    public List<string> ScanRoots { get; set; } = [];
    /// <summary>Process names hidden from the dev ports view (e.g. "Spotify").</summary>
    public List<string> HiddenPortProcesses { get; set; } = [];
}

public sealed class RegistryProject
{
    public string Path { get; set; } = "";
    /// <summary>Display name and id; defaults to the folder name.</summary>
    public string? Name { get; set; }
    public List<string> Tags { get; set; } = [];
    public bool Notifications { get; set; } = true;
    public RepoConfigFile Overrides { get; set; } = new();

    [JsonIgnore]
    public string EffectiveName => string.IsNullOrWhiteSpace(Name) ? System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) : Name!;
}

public static class Layers
{
    public const string LaunchJson = "launch.json";
    public const string DevServers = "devservers.json";
    public const string Central = "central";
    public const string Default = "default";
}

/// <summary>A service after the three layers are merged. Values may still contain ${...} variables.</summary>
public sealed class EffectiveService
{
    public required string Name { get; init; }
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public string Cwd { get; set; } = ".";
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int? Port { get; set; }
    public bool AutoPort { get; set; }
    public string? Url { get; set; }
    public string? Health { get; set; }
    public List<string> DependsOn { get; set; } = [];
    public List<PrepareStep> Prepare { get; set; } = [];
    public bool AutoRestart { get; set; }
    public int ReadyTimeoutSeconds { get; set; } = 120;
    public bool Disabled { get; set; }

    /// <summary>Field name → layer that supplied the effective value.</summary>
    public Dictionary<string, string> Origins { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when args, env, url or health reference ${port:...}, so the port can be moved.</summary>
    public bool UsesPortVariable =>
        Args.Any(ContainsPortVar) || Env.Values.Any(ContainsPortVar) || ContainsPortVar(Url) || ContainsPortVar(Health);

    private static bool ContainsPortVar(string? s) => s != null && s.Contains("${port:", StringComparison.OrdinalIgnoreCase);

    public string DisplayCommand => Args.Count == 0 ? Command : Command + " " + string.Join(' ', Args.Select(QuoteIfNeeded));

    internal static string QuoteIfNeeded(string a) => a.Length == 0 || a.Any(char.IsWhiteSpace) || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;
}

public sealed class EffectiveTask
{
    public required string Name { get; init; }
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public string Cwd { get; set; } = ".";
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Origins { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string DisplayCommand => Args.Count == 0 ? Command : Command + " " + string.Join(' ', Args.Select(EffectiveService.QuoteIfNeeded));
}

/// <summary>Merged configuration of one checkout (main or worktree) of a project.</summary>
public sealed class ProjectConfig
{
    public required string Project { get; init; }
    public required string Root { get; init; }
    public string? Worktree { get; init; }
    public Dictionary<string, EffectiveService> Services { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, EffectiveTask> Tasks { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Errors { get; } = [];
    public bool Valid => Errors.Count == 0;

    public string InstanceKey => Ids.Instance(Project, Worktree);
}

public static class Ids
{
    public static string Instance(string project, string? worktree) =>
        string.IsNullOrEmpty(worktree) ? project : project + "@" + worktree;

    public static string Service(string instanceKey, string service) => instanceKey + "/" + service;

    /// <summary>Splits "project@wt/svc" into (project, worktree, name). Returns false when there is no '/'.</summary>
    public static bool TryParse(string id, out string project, out string? worktree, out string name)
    {
        project = ""; worktree = null; name = "";
        var slash = id.IndexOf('/');
        if (slash <= 0 || slash == id.Length - 1) return false;
        var instance = id[..slash];
        name = id[(slash + 1)..];
        var at = instance.IndexOf('@');
        if (at < 0) project = instance;
        else { project = instance[..at]; worktree = instance[(at + 1)..]; }
        return project.Length > 0;
    }
}
