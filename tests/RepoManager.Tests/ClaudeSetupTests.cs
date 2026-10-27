using System.Diagnostics;
using System.Text.Json.Nodes;

namespace RepoManager.Tests;

public class ClaudeSetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-tests", "claude-" + Guid.NewGuid().ToString("N")[..8]);

    private const string Settings = """
        {
          // user comment
          "permissions": { "allow": ["Bash(git status)"] },
          "hooks": {
            "PreToolUse": [
              { "matcher": "Write", "hooks": [ { "type": "command", "command": "other-tool check" } ] }
            ]
          }
        }
        """;

    private const string ClaudeMd = "# Global instructions\n\nKeep this text.\n";

    public ClaudeSetupTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), Settings);
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), ClaudeMd);
    }

    private (int Code, string Out) Devm(string args)
    {
        var psi = new ProcessStartInfo(CliTests.DevmExe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.Environment["CLAUDE_CONFIG_DIR"] = _dir;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30000);
        return (p.ExitCode, o);
    }

    [Fact]
    public void Setup_is_idempotent_and_remove_restores_the_rest()
    {
        var setup = Devm("claude-setup --no-mcp --devm C:\\tools\\devm.exe");
        Assert.Equal(0, setup.Code);

        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "settings.json")))!;
        var pre = settings["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal(2, pre.Count);
        Assert.Equal("Write", pre[0]!["matcher"]!.GetValue<string>());
        Assert.Equal("Bash|PowerShell|mcp__Claude_Browser__preview_start", pre[1]!["matcher"]!.GetValue<string>());
        Assert.Equal("\"C:\\tools\\devm.exe\" hook", pre[1]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("Bash(git status)", settings["permissions"]!["allow"]![0]!.GetValue<string>());
        Assert.Single(Directory.GetFiles(_dir, "settings.json.bak-*"));

        var md = File.ReadAllText(Path.Combine(_dir, "CLAUDE.md"));
        Assert.StartsWith("# Global instructions", md);
        Assert.Contains("<!-- repomanager:start -->", md);

        // Second run changes nothing.
        var again = Devm("claude-setup --no-mcp --devm C:\\tools\\devm.exe");
        Assert.Contains("already present", again.Out);
        Assert.Contains("up to date", again.Out);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.bak-*"));

        var remove = Devm("claude-remove --no-mcp");
        Assert.Equal(0, remove.Code);
        var after = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "settings.json")))!;
        var left = Assert.Single(after["hooks"]!["PreToolUse"]!.AsArray());
        Assert.Equal("Write", left!["matcher"]!.GetValue<string>());
        Assert.Equal(ClaudeMd.TrimEnd(), File.ReadAllText(Path.Combine(_dir, "CLAUDE.md")).TrimEnd().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Setup_creates_files_when_missing()
    {
        File.Delete(Path.Combine(_dir, "settings.json"));
        File.Delete(Path.Combine(_dir, "CLAUDE.md"));
        Assert.Equal(0, Devm("claude-setup --no-mcp --devm C:\\x\\devm.exe").Code);
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "settings.json")))!["hooks"]);
        Assert.Contains("RepoManager", File.ReadAllText(Path.Combine(_dir, "CLAUDE.md")));
    }

    // ---- Agent Town hooks (run through devm.exe: the test project does not reference the CLI assembly) ----

    private static readonly string[] TownEvents =
        ["SessionStart", "UserPromptSubmit", "PostToolUse", "Notification", "Stop", "StopFailure", "SessionEnd"];

    private JsonNode ReadSettings() => JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "settings.json")))!;

    private static IEnumerable<JsonNode> Commands(JsonNode settings) =>
        settings["hooks"]?.AsObject().SelectMany(e => e.Value!.AsArray()).SelectMany(m => m!["hooks"]!.AsArray()).Select(h => h!)
        ?? [];

    [Fact]
    public void AddTownHooks_AddsEachEvent()
    {
        Assert.Equal(0, Devm("claude-setup --no-mcp --devm C:\\x\\devm.exe").Code);
        var hooks = ReadSettings()["hooks"]!;
        foreach (var e in TownEvents.Append("PermissionRequest"))
        {
            var entry = Assert.Single(hooks[e]!.AsArray())!;
            Assert.Null(entry["matcher"]);
            var h = Assert.Single(entry["hooks"]!.AsArray())!;
            Assert.Equal("command", h["type"]!.GetValue<string>());
            Assert.Equal("\"C:\\x\\devm.exe\" town-hook", h["command"]!.GetValue<string>());
        }
        Assert.Equal(600, hooks["PermissionRequest"]![0]!["hooks"]![0]!["timeout"]!.GetValue<int>());
        Assert.Null(hooks["PermissionRequest"]![0]!["hooks"]![0]!["async"]);
        Assert.Equal(2, hooks["Stop"]![0]!["hooks"]![0]!["timeout"]!.GetValue<int>());
        Assert.True(hooks["Stop"]![0]!["hooks"]![0]!["async"]!.GetValue<bool>());
        // SessionEnd runs synchronously so it is delivered before the session exits.
        Assert.Null(hooks["SessionEnd"]![0]!["hooks"]![0]!["async"]);
        // The user's own PreToolUse hook and the dev-server hook are untouched.
        var pre = hooks["PreToolUse"]!.AsArray();
        Assert.Equal(2, pre.Count);
        Assert.Equal("Write", pre[0]!["matcher"]!.GetValue<string>());
    }

    [Fact]
    public void AddTownHooks_Idempotent()
    {
        Devm("claude-setup --no-mcp --devm C:\\x\\devm.exe");
        var again = Devm("claude-setup --no-mcp --devm C:\\x\\devm.exe");
        Assert.Contains("town: already present", again.Out);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.bak-*"));
    }

    [Fact]
    public void AddTownHooks_NewDevmPath_ReplacesOldEntries()
    {
        Devm("claude-setup --no-mcp --devm C:\\x\\devm.exe");
        Devm("claude-setup --no-mcp --devm C:\\y\\devm.exe");
        var town = Commands(ReadSettings()).Where(h => h["command"]!.GetValue<string>().EndsWith(" town-hook")).ToList();
        Assert.Equal(TownEvents.Length + 1, town.Count);
        Assert.All(town, h => Assert.StartsWith("\"C:\\y\\", h["command"]!.GetValue<string>()));
    }

    [Fact]
    public void Setup_NoTown_SkipsTownHooks()
    {
        var setup = Devm("claude-setup --no-mcp --no-town --devm C:\\x\\devm.exe");
        Assert.Equal(0, setup.Code);
        Assert.DoesNotContain(Commands(ReadSettings()), h => h["command"]!.GetValue<string>().EndsWith(" town-hook"));
        Assert.NotNull(ReadSettings()["hooks"]!["PreToolUse"]);
    }

    [Fact]
    public void RemoveTownHooks_LeavesPreToolUseHookAndOthers()
    {
        Devm("claude-setup --no-mcp --devm C:\\x\\devm.exe");
        var off = Devm("claude-setup --no-mcp --no-town --devm C:\\x\\devm.exe");
        Assert.Contains("town: hooks removed", off.Out);
        var hooks = ReadSettings()["hooks"]!.AsObject();
        Assert.Equal(["PreToolUse"], hooks.Select(p => p.Key));
        Assert.Equal(2, hooks["PreToolUse"]!.AsArray().Count);
    }

    [Fact]
    public void RemoveHook_DoesNotTouchTownHooks()
    {
        Devm("claude-setup --no-mcp --devm C:\\x\\devm.exe");
        // A new devm path makes AddHook remove and re-add the dev-server hook; town hooks must survive it.
        Devm("claude-setup --no-mcp --no-town --devm C:\\x\\devm.exe");
        Devm("claude-setup --no-mcp --devm C:\\x\\devm.exe");
        var dev = Commands(ReadSettings()).Where(h => h["command"]!.GetValue<string>().EndsWith(" hook")).ToList();
        Assert.Single(dev);
        Assert.Equal(TownEvents.Length + 1, Commands(ReadSettings()).Count(h => h["command"]!.GetValue<string>().EndsWith(" town-hook")));
    }

    [Fact]
    public void Remove_RemovesTownHooks()
    {
        Devm("claude-setup --no-mcp --devm C:\\x\\devm.exe");
        Assert.Equal(0, Devm("claude-remove --no-mcp").Code);
        var after = ReadSettings();
        Assert.DoesNotContain(Commands(after), h => h["command"]!.GetValue<string>().Contains("devm"));
        Assert.Equal("Write", Assert.Single(after["hooks"]!["PreToolUse"]!.AsArray())!["matcher"]!.GetValue<string>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
