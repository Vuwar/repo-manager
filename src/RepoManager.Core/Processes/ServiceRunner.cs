using RepoManager.Contracts;
using RepoManager.Core.Config;
using RepoManager.Core.Logs;

namespace RepoManager.Core.Processes;

/// <summary>Runtime state of one service in one checkout. All transitions go through <see cref="Supervisor"/>.</summary>
public sealed class ServiceRunner
{
    public ServiceRunner(string id, ProjectConfig instance, EffectiveService config, LogBuffer log)
    {
        Id = id;
        Instance = instance;
        Config = config;
        Log = log;
    }

    public string Id { get; }
    public ProjectConfig Instance { get; set; }
    public EffectiveService Config { get; set; }
    public LogBuffer Log { get; }

    public string Name => Config.Name;
    public string InstanceKey => Instance.InstanceKey;
    public bool IsWorktree => Instance.Worktree != null;

    public ServiceState State { get; set; } = ServiceState.Stopped;
    public ManagedProcess? Process { get; set; }
    public int? Port { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public int RestartCount { get; set; }
    public string? LastError { get; set; }
    public int? LastExitCode { get; set; }
    public DateTimeOffset? UnhealthySince { get; set; }
    public bool UnhealthyNotified { get; set; }
    public List<DateTimeOffset> RecentCrashes { get; } = [];

    /// <summary>The command line actually launched (variables resolved), for display and hook matching.</summary>
    public string? ResolvedCommand { get; set; }

    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal CancellationTokenSource? RunCts { get; set; }

    /// <summary>Set while a user stop is in progress, so the exit is not reported as a crash.</summary>
    internal bool StopRequested { get; set; }

    public bool IsActive => State is ServiceState.Running or ServiceState.Unhealthy or ServiceState.Starting
        or ServiceState.Preparing or ServiceState.WaitingDeps or ServiceState.Stopping;

    public bool IsUp => State is ServiceState.Running or ServiceState.Unhealthy;

    /// <summary>Port-bearing services must take their port from ${port:...} to run in a worktree.</summary>
    public bool WorktreeReady => (Config.Port == null && !Config.AutoPort) || Config.UsesPortVariable;

    public ServiceDto ToDto(bool desired) => new()
    {
        Id = Id,
        Project = Instance.Project,
        Worktree = Instance.Worktree,
        Name = Name,
        State = State,
        Port = Port ?? Config.Port,
        Url = Url,
        Pid = Process?.Pid,
        StartedAt = StartedAt,
        RestartCount = RestartCount,
        LastError = LastError,
        LastExitCode = LastExitCode,
        WorktreeReady = WorktreeReady,
        AutoRestart = Config.AutoRestart,
        HasHealth = Config.Health != null,
        Disabled = Config.Disabled,
        Desired = desired,
        Command = ResolvedCommand ?? Config.DisplayCommand,
        DependsOn = Config.DependsOn,
    };
}

public sealed class TaskRunner
{
    public TaskRunner(string id, ProjectConfig instance, EffectiveTask config, LogBuffer log)
    {
        Id = id;
        Instance = instance;
        Config = config;
        Log = log;
    }

    public string Id { get; }
    public ProjectConfig Instance { get; set; }
    public EffectiveTask Config { get; set; }
    public LogBuffer Log { get; }
    public ManagedProcess? Process { get; set; }
    public bool Running => Process != null;
    public int? LastExitCode { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }

    public TaskDto ToDto() => new()
    {
        Id = Id,
        Name = Config.Name,
        Command = Config.DisplayCommand,
        Running = Running,
        LastExitCode = LastExitCode,
        LastRunAt = LastRunAt,
    };
}
