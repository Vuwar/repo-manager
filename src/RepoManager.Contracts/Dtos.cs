namespace RepoManager.Contracts;

public enum ServiceState
{
    Stopped,
    Preparing,
    WaitingDeps,
    Starting,
    Running,
    Unhealthy,
    Stopping,
    Crashed,
    Failed,
}

public enum LogStream
{
    Out,
    Err,
    Sys,
}

public sealed record ServiceDto
{
    public required string Id { get; init; }
    public required string Project { get; init; }
    public string? Worktree { get; init; }
    public required string Name { get; init; }
    public ServiceState State { get; init; }
    public int? Port { get; init; }
    public string? Url { get; init; }
    public int? Pid { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public int RestartCount { get; init; }
    public string? LastError { get; init; }
    public int? LastExitCode { get; init; }
    public bool WorktreeReady { get; init; }
    public bool AutoRestart { get; init; }
    public bool HasHealth { get; init; }
    public bool Disabled { get; init; }
    public bool Desired { get; init; }
    public string Command { get; init; } = "";
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}

public sealed record TaskDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Command { get; init; } = "";
    public bool Running { get; init; }
    public int? LastExitCode { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
}

public sealed record GitInfoDto
{
    public string? Branch { get; init; }
    public bool Dirty { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public string? Upstream { get; init; }
}

public sealed record InstanceDto
{
    /// <summary>Instance key: "project" for the main checkout, "project@worktree" for a worktree.</summary>
    public required string Key { get; init; }
    public string? Worktree { get; init; }
    public required string Root { get; init; }
    public GitInfoDto? Git { get; init; }
    public IReadOnlyList<ServiceDto> Services { get; init; } = [];
    public IReadOnlyList<TaskDto> Tasks { get; init; } = [];
}

public sealed record ProjectDto
{
    public required string Name { get; init; }
    public required string Root { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool Notifications { get; init; } = true;
    public bool Valid { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<InstanceDto> Instances { get; init; } = [];
}

public sealed record LogLineDto(DateTimeOffset Timestamp, LogStream Stream, string Text, string Source);

public sealed record ActionResultDto
{
    public bool Ok { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Affected { get; init; } = [];
    public IReadOnlyList<string> LogTail { get; init; } = [];

    public static ActionResultDto Success(string message, params string[] affected) =>
        new() { Ok = true, Message = message, Affected = affected };

    public static ActionResultDto Fail(string error, IReadOnlyList<string>? logTail = null) =>
        new() { Ok = false, Error = error, LogTail = logTail ?? [] };
}

public sealed record PortDto
{
    public int Port { get; init; }
    public int Pid { get; init; }
    public string? ProcessName { get; init; }
    public string? CommandLine { get; init; }
    public string? ManagedBy { get; init; }
    public string Address { get; init; } = "";
}

public sealed record ResolveResultDto
{
    public string? Project { get; init; }
    public string? Instance { get; init; }
    public string? Worktree { get; init; }
    public IReadOnlyList<string> ServiceIds { get; init; } = [];
    public string? Error { get; init; }
}

public sealed record ScanResultDto(string Path, string Name, bool Registered, IReadOnlyList<string> Markers);

public sealed record ConfigFieldDto(string Field, string? Value, string Origin);

public sealed record EffectiveServiceDto(string Name, IReadOnlyList<ConfigFieldDto> Fields);

public sealed record EffectiveConfigDto
{
    public required string Project { get; init; }
    public string? Worktree { get; init; }
    public IReadOnlyList<EffectiveServiceDto> Services { get; init; } = [];
    public IReadOnlyList<EffectiveServiceDto> Tasks { get; init; } = [];
    /// <summary>Raw JSON of the central (layer 3) overrides for this project.</summary>
    public string OverridesJson { get; init; } = "{}";
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed record DaemonInfoDto(string Version, int Pid, int Port, DateTimeOffset StartedAt);

public sealed record EventDto
{
    /// <summary>"status" (Payload = ServiceDto), "task" (TaskDto), "log" (LogLineDto), "projects" (no payload: project list changed).</summary>
    public required string Type { get; init; }
    public object? Payload { get; init; }
}

// ---- Requests ----

public sealed record ServiceActionRequest
{
    /// <summary>Full id ("panel-pro/api", "panel-pro@feat-x/api"), short name ("api", resolved with Cwd) or "all".</summary>
    public required string Target { get; init; }
    public string? Cwd { get; init; }
    public bool KillOwner { get; init; }
    public bool Force { get; init; }
}

public sealed record InstanceActionRequest(string Instance);

public sealed record GroupActionRequest(string Tag);

public sealed record TaskRunRequest
{
    /// <summary>Full task id ("panel-pro/publish") or short name resolved with Cwd.</summary>
    public required string Target { get; init; }
    public string? Cwd { get; init; }
}

public sealed record AddProjectRequest(string Path);

public sealed record RemoveProjectRequest(string Project);

public sealed record KillPidRequest(int Pid);

public sealed record OpenRequest(string Instance, string Tool);

public sealed record ProjectSettingsRequest
{
    public required string Project { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public bool? Notifications { get; init; }
    /// <summary>Full replacement of the layer 3 overrides, as devservers.json-shaped JSON.</summary>
    public string? OverridesJson { get; init; }
}

public sealed record HookRequest
{
    public required string ToolName { get; init; }
    public string? ToolInputJson { get; init; }
    public string? Cwd { get; init; }
}

public sealed record HookDecisionDto(bool Deny, string? Reason);
