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

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
