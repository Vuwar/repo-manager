using System.Text.Json;
using RepoManager.Contracts;

namespace RepoManager.Core.Agents;

/// <summary>A Claude Code hook event, parsed from a <see cref="TownHookEnvelope"/>. JSON fields are kept raw.</summary>
public sealed record AgentHookEvent(
    string Kind,
    string SessionId,
    string Cwd,
    string? Prompt,
    string? ToolName,
    string? ToolInputJson,
    string? Message,
    string? NotificationType,
    string? LastReply,
    string? PermissionSuggestionsJson,
    string? Socket,
    string? Token,
    string Surface,
    string? HostSessionId);

public static class HookEventParser
{
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "SessionStart", "UserPromptSubmit", "PostToolUse", "Notification", "PermissionRequest", "Stop", "StopFailure", "SessionEnd",
    };

    /// <summary>Null for bad JSON, an event the town does not use, or a missing or unsafe session id.</summary>
    public static AgentHookEvent? Parse(TownHookEnvelope env)
    {
        try
        {
            using var doc = JsonDocument.Parse(env.Json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var kind = Str(root, "hook_event_name");
            if (kind is null || !Kinds.Contains(kind)) return null;
            var id = Str(root, "session_id");
            if (!IsSafeId(id)) return null;

            return new AgentHookEvent(
                Kind: kind,
                SessionId: id!,
                Cwd: Str(root, "cwd") ?? "",
                Prompt: Str(root, "prompt"),
                ToolName: Str(root, "tool_name"),
                ToolInputJson: Raw(root, "tool_input"),
                Message: Str(root, "message"),
                NotificationType: Str(root, "notification_type"),
                LastReply: Str(root, "last_assistant_message"),
                PermissionSuggestionsJson: Raw(root, "permission_suggestions"),
                Socket: env.Socket,
                Token: env.Token,
                Surface: SurfaceFor(env.Entrypoint),
                HostSessionId: env.HostSessionId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string SurfaceFor(string? entrypoint) => entrypoint switch
    {
        "claude-desktop" or "claude-desktop-3p" => "desktop",
        "cli" => "terminal",
        _ => "other",
    };

    private static bool IsSafeId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 80
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Raw(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetRawText() : null;
}
