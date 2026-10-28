using System.Text.Json;
using System.Text.Json.Nodes;
using RepoManager.Contracts;
using RepoManager.Core.Agents;

namespace RepoManager.Cli;

/// <summary>
/// Claude Code hook for Agent Town. Forwards the hook's stdin JSON and the session environment (inbox address and
/// token, entrypoint, desktop session id) to the daemon. Never starts the daemon, never fails the hook.
///
/// Status events (SessionStart, UserPromptSubmit, PostToolUse, Notification, Stop, StopFailure, SessionEnd) give up
/// after 1 s and print nothing. PermissionRequest waits for a town answer (the daemon only holds it while the town
/// is focused) and prints a decision only when there is one; printing nothing lets the session show its own dialog.
///
/// PermissionRequest output, per the Claude Code hooks reference (code.claude.com/docs/en/hooks, checked 2026-10-27):
///   {"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}
///   "allow" may carry "updatedPermissions": an array of permission update entries; the input's
///   "permission_suggestions" uses the same entries, and a hook may echo them back ("Always OK"). Each entry's
///   "destination" (session, localSettings, projectSettings, userSettings) says where the rule is kept.
///   "deny" may carry "message" (tells Claude why) and "interrupt".
/// </summary>
public static class TownHook
{
    private static readonly TimeSpan StatusBudget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PermissionBudget = TimeSpan.FromSeconds(590);

    public static async Task<int> RunAsync()
    {
        try
        {
            using var status = new CancellationTokenSource(StatusBudget);
            // Raw bytes as UTF-8: the console input code page may not be UTF-8, and PowerShell pipes add a BOM.
            using var stdin = Console.OpenStandardInput();
            using var buffer = new MemoryStream();
            await stdin.CopyToAsync(buffer, status.Token);
            var input = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            var start = input.IndexOf('{'); // skips a BOM, or one mangled by a non-UTF-8 pipe
            if (start < 0) return 0;
            input = input[start..];

            string? kind;
            string? suggestions;
            using (var doc = JsonDocument.Parse(input))
            {
                var root = doc.RootElement;
                kind = root.TryGetProperty("hook_event_name", out var k) ? k.GetString() : null;
                suggestions = root.TryGetProperty("permission_suggestions", out var s) && s.ValueKind == JsonValueKind.Array ? s.GetRawText() : null;
            }

            var envelope = new TownHookEnvelope
            {
                Json = input,
                Socket = Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_SOCKET"),
                Token = Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_TOKEN"),
                Entrypoint = Environment.GetEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT"),
                HostSessionId = Environment.GetEnvironmentVariable("CLAUDE_CODE_HOST_SESSION_ID"),
                // Lets "Open window" find the session's window; a Toolhelp snapshot takes a few ms.
                ClaudePid = ProcessTree.FindAncestor(Environment.ProcessId, n => n.StartsWith("claude", StringComparison.OrdinalIgnoreCase)),
            };

            using var client = new DaemonClient();
            if (kind != "PermissionRequest")
            {
                if (!await client.ConnectAsync(start: false).WaitAsync(status.Token)) return 0;
                await client.PostAsync<ActionResultDto>("api/town/hook", envelope, status.Token);
                return 0;
            }

            using var wait = new CancellationTokenSource(PermissionBudget);
            if (!await client.ConnectAsync(start: false)) return 0;
            var decision = await client.PostAsync<PermissionDecisionDto>("api/town/permission", envelope, wait.Token);
            if (decision.Behavior is not ("allow" or "deny")) return 0;
            Console.WriteLine(DecisionJson(decision, suggestions));
            return 0;
        }
        catch (Exception ex)
        {
            // ex never holds the messaging token: it is only sent in a request body.
            if (Environment.GetEnvironmentVariable("REPOMANAGER_HOOK_DEBUG") == "1") Console.Error.WriteLine("devm town-hook: " + ex.Message);
            return 0;
        }
    }

    private static string DecisionJson(PermissionDecisionDto d, string? suggestionsJson)
    {
        var decision = new JsonObject { ["behavior"] = d.Behavior };
        if (d.Behavior == "allow" && d.Always && JsonNode.Parse(suggestionsJson ?? "null") is JsonArray updates)
        {
            // "Always OK" lasts for this session only; suggestions may point at a settings file.
            foreach (var u in updates.OfType<JsonObject>()) u["destination"] = "session";
            decision["updatedPermissions"] = updates;
        }
        if (d.Behavior == "deny")
            decision["message"] = "The user declined this in RepoManager Agent Town.";
        var output = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision },
        };
        return output.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
