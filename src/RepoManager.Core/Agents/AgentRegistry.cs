using RepoManager.Contracts;

namespace RepoManager.Core.Agents;

/// <summary>
/// In-memory list of Claude Code sessions, fed by hook events. Holds each session's inbox address and token
/// outside the DTOs so they never leave the daemon. Empty after a daemon restart: any later event recreates
/// its session, because the hook sends the inbox environment with every event.
/// </summary>
public sealed class AgentRegistry(Func<string, (string Project, string? Worktree)?> resolve, Func<DateTimeOffset> clock)
{
    private const int MessageMax = 140;
    private static readonly HashSet<string> WaitingNotifications =
        ["permission_prompt", "elicitation_dialog", "elicitation_url_dialog", "agent_needs_input"];

    private readonly object _gate = new();
    private readonly Dictionary<string, AgentSessionDto> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Socket, string Token)> _inboxes = new(StringComparer.Ordinal);

    public event Action<AgentSessionDto>? Changed;
    public event Action<string>? Ended;

    /// <summary>Returns the updated session, or null when the event ended it.</summary>
    public AgentSessionDto? Apply(AgentHookEvent e)
    {
        AgentSessionDto? updated = null;
        bool ended = false;
        lock (_gate)
        {
            if (e.Kind == "SessionEnd")
            {
                ended = _sessions.Remove(e.SessionId);
                _inboxes.Remove(e.SessionId);
            }
            else
            {
                if (!string.IsNullOrEmpty(e.Socket) && !string.IsNullOrEmpty(e.Token))
                    _inboxes[e.SessionId] = (e.Socket, e.Token);
                var now = clock();
                var current = _sessions.GetValueOrDefault(e.SessionId) ?? Create(e, now);
                updated = Next(current, e) with
                {
                    Surface = e.Surface,
                    HostSessionId = e.HostSessionId ?? current.HostSessionId,
                    Touched = now,
                    CanSend = _inboxes.ContainsKey(e.SessionId),
                };
                if (updated.State != current.State) updated = updated with { Since = now };
                _sessions[e.SessionId] = updated;
            }
        }
        if (ended) Ended?.Invoke(e.SessionId);
        if (updated != null) Changed?.Invoke(updated);
        return updated;
    }

    public IReadOnlyList<AgentSessionDto> Snapshot()
    {
        lock (_gate) return _sessions.Values.ToList();
    }

    public AgentSessionDto? Get(string sessionId)
    {
        lock (_gate) return _sessions.GetValueOrDefault(sessionId);
    }

    public (string Socket, string Token)? InboxFor(string sessionId)
    {
        lock (_gate) return _inboxes.TryGetValue(sessionId, out var inbox) ? inbox : null;
    }

    /// <summary>Replaces the session's permission prompts; any prompt makes the session wait on the user.</summary>
    public void SetPrompts(string sessionId, IReadOnlyList<PermissionPromptDto> prompts)
    {
        AgentSessionDto? updated;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var current)) return;
            updated = current with { Prompts = prompts };
            if (prompts.Count > 0 && current.State != "waiting")
                updated = updated with { State = "waiting", Message = "Needs permission: " + prompts[0].Summary, Since = clock() };
            _sessions[sessionId] = updated;
        }
        Changed?.Invoke(updated);
    }

    /// <summary>Drops sessions with no hook event for longer than maxAge. Returns how many were dropped.</summary>
    public int Sweep(TimeSpan maxAge)
    {
        List<string> removed;
        lock (_gate)
        {
            var cutoff = clock() - maxAge;
            removed = _sessions.Values.Where(s => s.Touched < cutoff).Select(s => s.Id).ToList();
            foreach (var id in removed)
            {
                _sessions.Remove(id);
                _inboxes.Remove(id);
            }
        }
        foreach (var id in removed) Ended?.Invoke(id);
        return removed.Count;
    }

    private AgentSessionDto Create(AgentHookEvent e, DateTimeOffset now)
    {
        var place = resolve(e.Cwd);
        return new AgentSessionDto
        {
            Id = e.SessionId,
            Project = place?.Project,
            Worktree = place?.Worktree,
            Folder = FolderName(e.Cwd),
            Cwd = e.Cwd,
            // A session first seen mid-turn (daemon restarted) is working; SessionStart sets idle below.
            State = "working",
            Surface = e.Surface,
            Since = now,
            Touched = now,
        };
    }

    private static AgentSessionDto Next(AgentSessionDto s, AgentHookEvent e) => e.Kind switch
    {
        "SessionStart" => s with { State = "idle", Message = "Session started" },
        "UserPromptSubmit" => s with { State = "working", Prompt = e.Prompt, Message = Clip(e.Prompt ?? "") },
        // done and failed stay until the next prompt.
        "PostToolUse" when s.State is not ("done" or "failed") => s with { State = "working", Message = "Used " + e.ToolName },
        "Notification" when e.NotificationType is { } t && WaitingNotifications.Contains(t) =>
            s with { State = "waiting", Message = Clip(e.Message ?? "Needs you") },
        "Stop" => s with { State = "done", LastReply = e.LastReply, Message = Clip(e.LastReply ?? "Done") },
        // StopFailure: the turn ended on an API error; last_assistant_message is the error text.
        "StopFailure" => s with { State = "failed", Message = Clip(e.LastReply ?? "Failed") },
        // PermissionRequest: the permission broker owns the waiting state.
        _ => s,
    };

    private static string Clip(string text) => text.Length <= MessageMax ? text : text[..MessageMax];

    private static string FolderName(string cwd)
    {
        var name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? cwd : name;
    }
}
