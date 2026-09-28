using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoManager.Cli;

/// <summary>
/// `devm claude-setup` / `devm claude-remove`: registers the MCP server, the PreToolUse hook and a marked block in
/// the global CLAUDE.md. Every change is reversible; settings.json is backed up before it is written.
/// </summary>
public static class ClaudeIntegration
{
    public const string McpName = "repomanager";
    public const string HookMatcher = "Bash|PowerShell|mcp__Claude_Browser__preview_start";
    public const string BlockStart = "<!-- repomanager:start -->";
    public const string BlockEnd = "<!-- repomanager:end -->";

    public const string ClaudeMdBlock = """
        <!-- repomanager:start -->
        ## Dev servers (RepoManager)

        Dev servers on this machine are managed by RepoManager and shared by the user and every Claude session.

        - Start, stop, restart and inspect dev servers only with `devm` (or the `repomanager` MCP tools), run from the repo or worktree folder:
          `devm status`, `devm start <svc>`, `devm restart <svc>`, `devm stop <svc>`, `devm logs <svc> --tail 100`, `devm url <svc>`.
        - Never kill dev server processes yourself (taskkill, Stop-Process, kill-port) and never run a managed server's command directly (`npm run dev`, `dotnet run`). A hook blocks these.
        - To preview a web app, get its URL with `devm url <svc>` and open that URL; do not start a second copy.
        - Inside a git worktree, `devm start <svc>` runs a separate copy of the service on its own ports.
        <!-- repomanager:end -->
        """;

    public static string ClaudeDir =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static int Setup(string devmPath, bool skipMcp)
    {
        Directory.CreateDirectory(ClaudeDir);
        var hookCommand = $"\"{devmPath}\" hook";

        var settingsPath = Path.Combine(ClaudeDir, "settings.json");
        var changed = UpdateSettings(settingsPath, root => AddHook(root, hookCommand));
        Console.WriteLine(changed ? $"hook: added PreToolUse hook to {settingsPath}" : "hook: already present");

        var mdPath = Path.Combine(ClaudeDir, "CLAUDE.md");
        Console.WriteLine(WriteBlock(mdPath) ? $"CLAUDE.md: added RepoManager block to {mdPath}" : "CLAUDE.md: block up to date");

        if (!skipMcp)
        {
            RunClaude($"mcp remove --scope user {McpName}", quiet: true);
            var ok = RunClaude($"mcp add --scope user {McpName} -- \"{devmPath}\" mcp", quiet: false);
            Console.WriteLine(ok
                ? $"mcp: registered '{McpName}' (user scope)"
                : $"mcp: could not run 'claude'. Register it yourself: claude mcp add --scope user {McpName} -- \"{devmPath}\" mcp");
        }
        return 0;
    }

    public static int Remove(bool skipMcp)
    {
        var settingsPath = Path.Combine(ClaudeDir, "settings.json");
        if (File.Exists(settingsPath))
            Console.WriteLine(UpdateSettings(settingsPath, RemoveHook) ? "hook: removed" : "hook: not present");
        var mdPath = Path.Combine(ClaudeDir, "CLAUDE.md");
        if (File.Exists(mdPath))
            Console.WriteLine(RemoveBlock(mdPath) ? "CLAUDE.md: block removed" : "CLAUDE.md: no block");
        if (!skipMcp)
            Console.WriteLine(RunClaude($"mcp remove --scope user {McpName}", quiet: true) ? "mcp: removed" : "mcp: not registered (or 'claude' not found)");
        return 0;
    }

    // ---- settings.json ----

    /// <summary>Loads, mutates and (only when something changed) backs up and saves settings.json.</summary>
    public static bool UpdateSettings(string path, Func<JsonObject, bool> mutate)
    {
        JsonObject root;
        var original = File.Exists(path) ? File.ReadAllText(path) : null;
        if (string.IsNullOrWhiteSpace(original)) root = new JsonObject();
        else
        {
            var node = JsonNode.Parse(original, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            root = node as JsonObject ?? throw new InvalidOperationException($"{path} is not a JSON object");
        }
        if (!mutate(root)) return false;
        if (original != null) File.WriteAllText(path + $".bak-{DateTime.Now:yyyyMMdd-HHmmss}", original);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
        return true;
    }

    private static bool IsOurHook(JsonNode? h) =>
        h?["command"]?.GetValue<string>() is { } c && c.Contains("devm", StringComparison.OrdinalIgnoreCase) && c.TrimEnd().EndsWith(" hook", StringComparison.OrdinalIgnoreCase);

    public static bool AddHook(JsonObject root, string command)
    {
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        var pre = hooks["PreToolUse"] as JsonArray ?? new JsonArray();
        var existing = pre.OfType<JsonObject>().FirstOrDefault(m => (m["hooks"] as JsonArray)?.Any(IsOurHook) == true);
        if (existing != null &&
            existing["matcher"]?.GetValue<string>() == HookMatcher &&
            (existing["hooks"] as JsonArray)!.Any(h => h?["command"]?.GetValue<string>() == command))
            return false;
        RemoveHook(root);
        hooks = root["hooks"] as JsonObject ?? new JsonObject();
        pre = hooks["PreToolUse"] as JsonArray ?? new JsonArray();
        pre.Add(new JsonObject
        {
            ["matcher"] = HookMatcher,
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = 5 }),
        });
        hooks["PreToolUse"] = pre;
        root["hooks"] = hooks;
        return true;
    }

    public static bool RemoveHook(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks || hooks["PreToolUse"] is not JsonArray pre) return false;
        var changed = false;
        foreach (var m in pre.OfType<JsonObject>().ToList())
        {
            if (m["hooks"] is not JsonArray list) continue;
            foreach (var h in list.Where(IsOurHook).ToList()) { list.Remove(h); changed = true; }
            if (list.Count == 0) pre.Remove(m);
        }
        if (pre.Count == 0) hooks.Remove("PreToolUse");
        if (hooks.Count == 0) root.Remove("hooks");
        return changed;
    }

    // ---- CLAUDE.md ----

    public static bool WriteBlock(string path)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var block = ClaudeMdBlock.ReplaceLineEndings(Environment.NewLine);
        var updated = ReplaceBlock(text, block);
        if (updated == text) return false;
        File.WriteAllText(path, updated);
        return true;
    }

    public static bool RemoveBlock(string path)
    {
        var text = File.ReadAllText(path);
        var updated = ReplaceBlock(text, null);
        if (updated == text) return false;
        File.WriteAllText(path, updated);
        return true;
    }

    /// <summary>Replaces (or removes, when block is null) the marked block; appends it when missing.</summary>
    public static string ReplaceBlock(string text, string? block)
    {
        var s = text.IndexOf(BlockStart, StringComparison.Ordinal);
        var e = s < 0 ? -1 : text.IndexOf(BlockEnd, s, StringComparison.Ordinal);
        if (s >= 0 && e > s)
        {
            var before = text[..s];
            var after = text[(e + BlockEnd.Length)..];
            if (block == null)
                return (before.TrimEnd() + (after.Trim().Length > 0 ? Environment.NewLine + Environment.NewLine + after.TrimStart() : Environment.NewLine)).TrimStart();
            return before + block + after;
        }
        if (block == null) return text;
        var sep = text.Length == 0 ? "" : text.EndsWith('\n') ? Environment.NewLine : Environment.NewLine + Environment.NewLine;
        return text + sep + block + Environment.NewLine;
    }

    private static bool RunClaude(string args, bool quiet)
    {
        try
        {
            // claude is usually a .cmd shim, so go through cmd.exe.
            var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"claude {args}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60000)) { p.Kill(true); return false; }
            if (!quiet && p.ExitCode != 0) Console.Error.WriteLine((o.Result + e.Result).Trim());
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
