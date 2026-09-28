using System.Text.Json;
using RepoManager.Contracts;

namespace RepoManager.Cli;

/// <summary>
/// Claude Code PreToolUse hook. Reads the hook JSON from stdin, asks the daemon, and prints a deny decision
/// when the call would kill or duplicate a managed dev server. Fails open: no daemon, bad input, slow answer
/// or any error means the tool call goes ahead.
/// </summary>
public static class Hook
{
    public static async Task<int> RunAsync()
    {
        try
        {
            using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            // Raw bytes as UTF-8: the console input code page may not be UTF-8, and PowerShell pipes add a BOM.
            using var stdin = Console.OpenStandardInput();
            using var buffer = new MemoryStream();
            await stdin.CopyToAsync(buffer, overall.Token);
            var input = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            var start = input.IndexOf('{'); // skips a BOM, or one mangled by a non-UTF-8 pipe
            if (start < 0) return 0;
            input = input[start..];
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            var tool = root.TryGetProperty("tool_name", out var t) ? t.GetString() : null;
            if (tool is not ("Bash" or "PowerShell" or "mcp__Claude_Browser__preview_start")) return 0;
            var toolInput = root.TryGetProperty("tool_input", out var ti) ? ti.GetRawText() : "{}";
            var cwd = root.TryGetProperty("cwd", out var c) ? c.GetString() : Environment.CurrentDirectory;

            using var client = new DaemonClient();
            if (!await client.ConnectAsync(start: false)) return 0;
            var decision = await client.PostAsync<HookDecisionDto>("api/hook",
                new HookRequest { ToolName = tool, ToolInputJson = toolInput, Cwd = cwd }, overall.Token);
            if (!decision.Deny) return 0;

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "PreToolUse",
                    permissionDecision = "deny",
                    permissionDecisionReason = "RepoManager: " + decision.Reason,
                },
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return 0;
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("REPOMANAGER_HOOK_DEBUG") == "1") Console.Error.WriteLine("devm hook: " + ex);
            return 0;
        }
    }
}
