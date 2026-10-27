using System.Text.Json;
using RepoManager.Contracts;
using RepoManager.Core.Agents;

namespace RepoManager.Tests;

public class AgentRegistryTests
{
    private DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly AgentRegistry _reg;

    public AgentRegistryTests()
    {
        _reg = new AgentRegistry(
            cwd => cwd.Equals(@"C:\r\demo", StringComparison.OrdinalIgnoreCase) ? ("demo", null) : null,
            () => _now);
    }

    private static AgentHookEvent Ev(string kind, string id = "s1", string cwd = @"C:\r\demo", string? prompt = null,
        string? tool = null, string? notificationType = null, string? lastReply = null,
        string? socket = null, string? token = null) =>
        new(kind, id, cwd, prompt, tool, null, null, notificationType, lastReply, null, socket, token, "desktop", null);

    [Fact]
    public void SessionStart_CreatesIdle()
    {
        var s = _reg.Apply(Ev("SessionStart"))!;
        Assert.Equal("idle", s.State);
        Assert.Equal("Session started", s.Message);
        Assert.Equal("demo", s.Project);
        Assert.Equal("demo", s.Folder);
        Assert.Equal("desktop", s.Surface);
    }

    [Fact]
    public void UserPromptSubmit_Working_WithPrompt()
    {
        var prompt = new string('x', 200);
        var s = _reg.Apply(Ev("UserPromptSubmit", prompt: prompt))!;
        Assert.Equal("working", s.State);
        Assert.Equal(prompt, s.Prompt);
        Assert.Equal(140, s.Message.Length);
    }

    [Fact]
    public void PostToolUse_Working_MessageUsedTool()
    {
        var s = _reg.Apply(Ev("PostToolUse", tool: "Bash"))!;
        Assert.Equal("working", s.State);
        Assert.Equal("Used Bash", s.Message);
    }

    [Theory]
    [InlineData("permission_prompt")]
    [InlineData("elicitation_dialog")]
    [InlineData("elicitation_url_dialog")]
    [InlineData("agent_needs_input")]
    public void Notification_PermissionOrElicitation_Waiting(string type)
    {
        _reg.Apply(Ev("UserPromptSubmit", prompt: "go"));
        Assert.Equal("waiting", _reg.Apply(Ev("Notification", notificationType: type))!.State);
    }

    [Fact]
    public void Notification_IdlePrompt_LeavesState()
    {
        _reg.Apply(Ev("Stop", lastReply: "ok"));
        Assert.Equal("done", _reg.Apply(Ev("Notification", notificationType: "idle_prompt"))!.State);
    }

    [Fact]
    public void Stop_Done_KeepsUntilNextPrompt()
    {
        _reg.Apply(Ev("UserPromptSubmit", prompt: "go"));
        var s = _reg.Apply(Ev("Stop", lastReply: "Finished."))!;
        Assert.Equal("done", s.State);
        Assert.Equal("Finished.", s.LastReply);
        Assert.Equal("done", _reg.Apply(Ev("PostToolUse", tool: "Bash"))!.State);
        Assert.Equal("working", _reg.Apply(Ev("UserPromptSubmit", prompt: "more"))!.State);
    }

    [Fact]
    public void StopFailure_Failed_KeepsUntilNextPrompt()
    {
        _reg.Apply(Ev("UserPromptSubmit", prompt: "go"));
        var s = _reg.Apply(Ev("StopFailure", lastReply: "API Error: Rate limit reached"))!;
        Assert.Equal("failed", s.State);
        Assert.Equal("API Error: Rate limit reached", s.Message);
        Assert.Equal("failed", _reg.Apply(Ev("PostToolUse", tool: "Bash"))!.State);
        Assert.Equal("working", _reg.Apply(Ev("UserPromptSubmit", prompt: "retry"))!.State);
    }

    [Fact]
    public void Since_ChangesOnlyOnStateChange()
    {
        var first = _reg.Apply(Ev("PostToolUse", tool: "Bash"))!;
        _now += TimeSpan.FromMinutes(1);
        var second = _reg.Apply(Ev("PostToolUse", tool: "Read"))!;
        Assert.Equal(first.Since, second.Since);
        Assert.Equal(_now, second.Touched);
    }

    [Fact]
    public void SessionEnd_RemovesAndRaisesEnded()
    {
        string? ended = null;
        _reg.Ended += id => ended = id;
        _reg.Apply(Ev("SessionStart", socket: "p", token: "t"));
        Assert.Null(_reg.Apply(Ev("SessionEnd")));
        Assert.Equal("s1", ended);
        Assert.Empty(_reg.Snapshot());
        Assert.Null(_reg.InboxFor("s1"));
    }

    [Fact]
    public void Apply_RaisesChanged()
    {
        AgentSessionDto? changed = null;
        _reg.Changed += s => changed = s;
        _reg.Apply(Ev("SessionStart"));
        Assert.Equal("s1", changed?.Id);
    }

    [Fact]
    public void UnknownFolder_StillListed()
    {
        var s = _reg.Apply(Ev("SessionStart", cwd: @"D:\elsewhere\tool"))!;
        Assert.Null(s.Project);
        Assert.Equal("tool", s.Folder);
        Assert.Single(_reg.Snapshot());
    }

    [Fact]
    public void FirstEventAfterRestart_RecreatesWithInbox()
    {
        var s = _reg.Apply(Ev("PostToolUse", tool: "Bash", socket: @"\\.\pipe\x", token: "tok"))!;
        Assert.Equal("working", s.State);
        Assert.True(s.CanSend);
        Assert.Equal((@"\\.\pipe\x", "tok"), _reg.InboxFor("s1"));
    }

    [Fact]
    public void PermissionRequest_UnknownSession_CreatedWorking_KnownUnchanged()
    {
        Assert.Equal("working", _reg.Apply(Ev("PermissionRequest", tool: "Bash"))!.State);
        _reg.Apply(Ev("Stop", lastReply: "x"));
        Assert.Equal("done", _reg.Apply(Ev("PermissionRequest", tool: "Bash"))!.State);
    }

    [Fact]
    public void SetPrompts_NonEmpty_SetsWaitingAndListsPrompts()
    {
        _reg.Apply(Ev("UserPromptSubmit", prompt: "go"));
        AgentSessionDto? changed = null;
        _reg.Changed += s => changed = s;
        _reg.SetPrompts("s1", [new PermissionPromptDto { RequestId = "r1", ToolName = "Bash", Summary = "ls", Answerable = true }]);
        var s = _reg.Snapshot().Single();
        Assert.Equal("waiting", s.State);
        Assert.Single(s.Prompts);
        Assert.Equal("waiting", changed?.State);
    }

    [Fact]
    public void Token_NeverInDto()
    {
        _reg.Apply(Ev("SessionStart", socket: @"\\.\pipe\x", token: "secret-token-123"));
        var json = JsonSerializer.Serialize(_reg.Snapshot(), Protocol.Json);
        Assert.DoesNotContain("secret-token-123", json);
    }

    [Fact]
    public void Sweep_DropsAfterMaxAge()
    {
        string? ended = null;
        _reg.Ended += id => ended = id;
        _reg.Apply(Ev("SessionStart"));
        _now += TimeSpan.FromHours(8) - TimeSpan.FromSeconds(1);
        Assert.Equal(0, _reg.Sweep(TimeSpan.FromHours(8)));
        _now += TimeSpan.FromSeconds(2);
        Assert.Equal(1, _reg.Sweep(TimeSpan.FromHours(8)));
        Assert.Equal("s1", ended);
        Assert.Empty(_reg.Snapshot());
    }
}
