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
    /// <summary>The session's Claude process (nearest claude*.exe above the hook), used to bring its window forward.</summary>
    public int? ClaudePid { get; init; }
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
    /// <summary>The session's title (the desktop app's name for it), else its first prompt shortened.</summary>
    public string? Title { get; init; }
    /// <summary>Every assistant message of the last turn, oldest first, separated by blank lines; null when unknown.</summary>
    public string? LastTurn { get; init; }
    /// <summary>A question the session left for the user with suggested replies (the desktop app's turn wrap-up card).</summary>
    public WrapUpDto? WrapUp { get; init; }
}

/// <summary>Claude's end-of-turn summary and question, with replies the user can pick (each sends its Message).</summary>
public sealed record WrapUpDto
{
    public string? Recap { get; init; }
    public string? Ask { get; init; }
    public IReadOnlyList<WrapUpActionDto> Actions { get; init; } = [];
}

public sealed record WrapUpActionDto(string Label, string Message);

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

public sealed record TownOpenRequest(string SessionId);

/// <summary>XP and level: Level starts at LevelStart XP; the next level starts at NextLevel.</summary>
public sealed record LevelDto(int Xp, int Level, int LevelStart, int NextLevel);

/// <summary>The user's level and every repo's (by project name).</summary>
public sealed record TownProgressDto(LevelDto Player, IReadOnlyDictionary<string, LevelDto> Repos);

/// <summary>One award: Kind is "answer" (a prompt answered from the town), "send" (a town message got a session working) or "turn" (a repo's session finished a turn).</summary>
public sealed record XpAwardDto(string Kind, int Amount, string? SessionId, string? Repo);

/// <summary>Agent Town attention settings and whether claude-setup installs the town hooks. All on by default.</summary>
public sealed record TownSettingsDto(bool Tray, bool Notifications, bool Sound, bool Hooks);

/// <summary>A desktop session by its CLAUDE_CODE_HOST_SESSION_ID (local_...), e.g. one linked from a reply.</summary>
public sealed record TownOpenHostRequest(string HostSessionId);
