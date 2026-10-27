namespace RepoManager.Contracts;

/// <summary>What <c>devm town-hook</c> posts: the hook's stdin JSON plus the session environment it ran in.</summary>
public sealed record TownHookEnvelope
{
    public required string Json { get; init; }
    /// <summary>CLAUDE_CODE_MESSAGING_SOCKET. Kept in daemon memory only.</summary>
    public string? Socket { get; init; }
    /// <summary>CLAUDE_CODE_MESSAGING_TOKEN. Kept in daemon memory only; never returned, logged or published.</summary>
    public string? Token { get; init; }
    public string? Entrypoint { get; init; }
    public string? HostSessionId { get; init; }
}

/// <summary>One Claude Code session on this machine. State: idle, working, waiting, done, failed.</summary>
public sealed record AgentSessionDto
{
    public required string Id { get; init; }
    public string? Project { get; init; }
    public string? Worktree { get; init; }
    /// <summary>Last segment of <see cref="Cwd"/>; names the temporary building when Project is null.</summary>
    public required string Folder { get; init; }
    public required string Cwd { get; init; }
    public required string State { get; init; }
    public string Message { get; init; } = "";
    public string? Prompt { get; init; }
    public string? LastReply { get; init; }
    /// <summary>desktop, terminal or other.</summary>
    public required string Surface { get; init; }
    public string? HostSessionId { get; init; }
    /// <summary>When the current state began.</summary>
    public DateTimeOffset Since { get; init; }
    /// <summary>Last hook event for this session.</summary>
    public DateTimeOffset Touched { get; init; }
    /// <summary>The daemon knows this session's inbox, so a message can be sent to it.</summary>
    public bool CanSend { get; init; }
    public IReadOnlyList<PermissionPromptDto> Prompts { get; init; } = [];
}

public sealed record PermissionPromptDto
{
    public required string RequestId { get; init; }
    public required string ToolName { get; init; }
    public required string Summary { get; init; }
    /// <summary>False when the prompt arrived while the town was not focused: answer it in the session window.</summary>
    public bool Answerable { get; init; }
}

/// <summary>Behavior is "allow", "deny" or null for no decision (the session shows its own dialog).</summary>
public sealed record PermissionDecisionDto(string? Behavior, bool Always);

public sealed record TownSendRequest(string SessionId, string Text);

public sealed record PermissionAnswerRequest(string RequestId, string Behavior, bool Always);

public sealed record TownFocusRequest(bool Focused);
