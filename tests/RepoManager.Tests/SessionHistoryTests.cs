using System.Text.Json;
using RepoManager.Core.Agents;

namespace RepoManager.Tests;

public class SessionHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rm-tests", "sh-" + Guid.NewGuid().ToString("N")[..8]);
    private string Projects => Path.Combine(_root, "projects");
    private string Desktop => Path.Combine(_root, "desktop");
    private string Demo => Path.Combine(_root, "repos", "demo");
    private string Other => Path.Combine(_root, "repos", "other");

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    public SessionHistoryTests()
    {
        Directory.CreateDirectory(Projects);
        Directory.CreateDirectory(Desktop);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private SessionHistory History() => new(Projects, Desktop, cwd =>
        cwd.StartsWith(Demo, StringComparison.OrdinalIgnoreCase) ? ("demo", cwd.Contains("wt-a") ? "wt-a" : null)
        : cwd.StartsWith(Other, StringComparison.OrdinalIgnoreCase) ? ("other", null) : null);

    private static string NewId() => Guid.NewGuid().ToString();

    private static string User(string text, string cwd, DateTimeOffset at, string entrypoint = "cli", bool sidechain = false) =>
        JsonSerializer.Serialize(new { type = "user", isSidechain = sidechain, cwd, entrypoint, timestamp = at, message = new { role = "user", content = text } });

    private static string Assistant(string text, DateTimeOffset at) =>
        JsonSerializer.Serialize(new { type = "assistant", timestamp = at, message = new { role = "assistant", content = new[] { new { type = "text", text } } } });

    private static string Tool(string name, object input, DateTimeOffset at) =>
        JsonSerializer.Serialize(new { type = "assistant", timestamp = at, message = new { role = "assistant", content = new[] { new { type = "tool_use", name, input } } } });

    private static string ToolResult() =>
        JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = new[] { new { type = "tool_result", content = "ok" } } } });

    private static string Line(string type, string field, string value) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["type"] = type, [field] = value });

    private string WriteTranscript(string id, params string[] lines)
    {
        var dir = Path.Combine(Projects, "C--repos-demo");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, id + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private void WriteIndex(string cliId, string hostId, string title, bool archived = false, DateTimeOffset? last = null)
    {
        var dir = Path.Combine(Desktop, "acct", "org");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, hostId + ".json"), JsonSerializer.Serialize(new
        {
            sessionId = hostId, cliSessionId = cliId, cwd = Demo, title, isArchived = archived,
            createdAt = T0.ToUnixTimeMilliseconds(), lastActivityAt = (last ?? T0).ToUnixTimeMilliseconds(),
        }));
    }

    private static readonly HashSet<string> NoLive = [];

    [Fact]
    public void List_ReturnsProjectSessionsNewestFirst_WithTitles()
    {
        string a = NewId(), b = NewId(), c = NewId(), d = NewId();
        WriteTranscript(a, User("first prompt that is quite long and goes on past forty-eight characters", Demo, T0));
        WriteTranscript(b, Line("ai-title", "aiTitle", "AI title"), User("p", Demo, T0.AddHours(1)), Line("agent-name", "agentName", "Agent name"));
        WriteTranscript(c, Line("ai-title", "aiTitle", "AI title"), User("p", Demo, T0.AddHours(2)), Line("custom-title", "customTitle", "Custom"));
        WriteTranscript(d, Line("ai-title", "aiTitle", "AI only"), User("p", Demo, T0.AddHours(3)));

        var list = History().List("demo", NoLive);

        Assert.Equal([d, c, b, a], list.Select(s => s.Id));
        Assert.Equal(["AI only", "Custom", "Agent name", "first prompt that is quite long and goes on past"], list.Select(s => s.Title));
        Assert.All(list, s => Assert.Equal("terminal", s.Surface));
        Assert.Equal(T0.AddHours(3), list[0].LastActivity);
        Assert.Equal(T0, list[3].Started);
    }

    [Fact]
    public void List_MarksDesktopFromIndex_WithHostIdAndArchived()
    {
        var id = NewId();
        WriteTranscript(id, User("p", Demo, T0, entrypoint: "claude-desktop"));
        WriteIndex(id, "local_abc-1", "Desktop title", archived: true, last: T0.AddDays(1));

        var s = Assert.Single(History().List("demo", NoLive));

        Assert.Equal("desktop", s.Surface);
        Assert.Equal("local_abc-1", s.HostSessionId);
        Assert.True(s.Archived);
        Assert.Equal("Desktop title", s.Title);
        Assert.Equal(T0.AddDays(1), s.LastActivity);
    }

    [Fact]
    public void List_SkipsLiveIds_SubagentFolders_NoPromptFiles_AndOtherProjects()
    {
        string live = NewId(), sub = NewId(), empty = NewId(), other = NewId(), side = NewId(), keep = NewId(), wt = NewId();
        WriteTranscript(live, User("p", Demo, T0));
        var subDir = Path.Combine(Projects, "C--repos-demo", live, "subagents");
        Directory.CreateDirectory(subDir);
        File.WriteAllLines(Path.Combine(subDir, sub + ".jsonl"), [User("p", Demo, T0)]);
        WriteTranscript(empty, Line("ai-title", "aiTitle", "x"), ToolResult());
        WriteTranscript(other, User("p", Other, T0));
        WriteTranscript(side, User("p", Demo, T0, sidechain: true));
        WriteTranscript(keep, User("p", Demo, T0));
        WriteTranscript(wt, User("p", Path.Combine(Demo, "wt-a"), T0));

        var list = History().List("demo", new HashSet<string> { live });

        Assert.Equal(new[] { keep, wt }.Order(), list.Select(s => s.Id).Order());
        Assert.Equal("wt-a", list.Single(s => s.Id == wt).Worktree);
    }

    [Fact]
    public void List_TranscriptWithoutIndex_IsTerminalForCli_ElseDesktopWithoutHostId()
    {
        string cli = NewId(), desk = NewId();
        WriteTranscript(cli, User("p", Demo, T0));
        WriteTranscript(desk, User("p", Demo, T0.AddMinutes(1), entrypoint: "claude-desktop"));

        var list = History().List("demo", NoLive);

        Assert.Equal("terminal", list.Single(s => s.Id == cli).Surface);
        var d = list.Single(s => s.Id == desk);
        Assert.Equal("desktop", d.Surface);
        Assert.Null(d.HostSessionId);
    }

    [Fact]
    public void List_IndexWithoutTranscript_IsNotListed()
    {
        WriteIndex(NewId(), "local_gone", "Gone");
        Assert.Empty(History().List("demo", NoLive));
    }

    [Fact]
    public void List_LargeFile_ReadsHeadAndTailOnly()
    {
        var id = NewId();
        var pad = JsonSerializer.Serialize(new { type = "progress", data = new string('x', 1000) });
        var lines = new List<string> { User("p", Demo, T0) };
        lines.AddRange(Enumerable.Repeat(pad, 10_000));
        lines.Add(Line("custom-title", "customTitle", "Late title"));
        WriteTranscript(id, [.. lines]);
        var h = History();

        Assert.Equal("Late title", Assert.Single(h.List("demo", NoLive)).Title);
        Assert.True(h.BytesRead < 200_000, $"read {h.BytesRead} bytes");
        var before = h.BytesRead;
        h.List("demo", NoLive);
        Assert.Equal(before, h.BytesRead);
    }

    private string LongSession(int prompts)
    {
        var id = NewId();
        var lines = new List<string>();
        for (var i = 0; i < prompts; i++)
        {
            lines.Add(User("prompt " + i, Demo, T0.AddMinutes(i)));
            lines.Add(Assistant("reply " + i, T0.AddMinutes(i)));
        }
        WriteTranscript(id, [.. lines]);
        return id;
    }

    [Fact]
    public void Page_ReturnsLast150_AndEarlierWithBefore()
    {
        var id = LongSession(200);
        var h = History();

        var page = h.Page(id, null)!;
        Assert.Equal(400, page.Total);
        Assert.Equal(250, page.Start);
        Assert.Equal(150, page.Entries.Count);
        Assert.Equal("reply 199", page.Entries[^1].Text);

        var earlier = h.Page(id, 250)!;
        Assert.Equal(100, earlier.Start);
        Assert.Equal(150, earlier.Entries.Count);
        Assert.Equal("user", earlier.Entries[0].Role);
    }

    [Fact]
    public void Page_ToolEntries_AreShortLines()
    {
        var id = NewId();
        WriteTranscript(id, User("go", Demo, T0),
            Tool("Bash", new { command = "npm test" }, T0), ToolResult(),
            Tool("Edit", new { file_path = @"C:\repos\demo\web\App.tsx" }, T0), ToolResult(),
            Tool("Grep", new { pattern = "x" }, T0), ToolResult(),
            Assistant("done", T0));

        var page = History().Page(id, null)!;

        Assert.Equal(["user", "tool", "tool", "tool", "assistant"], page.Entries.Select(e => e.Role));
        Assert.Equal(["go", "Ran npm test", "Edited App.tsx", "Used Grep", "done"], page.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Handoff_FollowsFormat_AndCapsAt4000()
    {
        var id = LongSession(30);
        var h = History();

        var text = h.Handoff(id)!;
        var lines = text.Split('\n');

        Assert.Equal($"This continues an earlier session: \"prompt 0\" ({T0.ToLocalTime():yyyy-MM-dd HH:mm}).", lines[0]);
        Assert.Contains("What I asked:", lines);
        Assert.Contains("- (… 15 earlier)", lines);
        Assert.Contains("- prompt 29", lines);
        Assert.DoesNotContain("- prompt 14", lines);
        Assert.Contains("Your last reply:", lines);
        Assert.Contains("reply 29", lines);
        Assert.Equal("Full transcript (read it only if you need details): " + h.TranscriptPath(id), lines[^1]);
        Assert.True(text.Length <= 4000);
    }

    [Fact]
    public void Handoff_LongPromptsAndReply_StayUnder4000()
    {
        var id = NewId();
        var lines = new List<string>();
        for (var i = 0; i < 15; i++) lines.Add(User(new string('p', 5000), Demo, T0));
        lines.Add(Assistant(new string('r', 9000), T0));
        WriteTranscript(id, [.. lines]);

        var text = History().Handoff(id)!;

        Assert.True(text.Length <= 4000, $"{text.Length}");
        Assert.EndsWith(".jsonl", text);
    }

    [Theory]
    [InlineData(@"..\x")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData(null)]
    public void InvalidId_OrPathLikeId_ReturnsNull(string? id)
    {
        var h = History();
        Assert.False(SessionHistory.ValidId(id));
        Assert.Null(h.Page(id!, null));
        Assert.Null(h.Handoff(id!));
        Assert.Null(h.Find(id!));
    }

    [Fact]
    public void CorruptLines_AreSkipped()
    {
        var id = NewId();
        WriteTranscript(id, "{not json", User("p", Demo, T0), "[1,2]", "\"str\"", Assistant("a", T0), "{\"type\":\"user\",\"message\":5}");

        var page = History().Page(id, null)!;

        Assert.Equal(["p", "a"], page.Entries.Select(e => e.Text));
        Assert.Single(History().List("demo", NoLive));
    }
}
