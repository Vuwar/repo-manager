using RepoManager.Contracts;
using RepoManager.Core.Agents;

namespace RepoManager.Tests;

public class AgentParserTests
{
    private static AgentHookEvent? Parse(string json, string? entrypoint = null) =>
        HookEventParser.Parse(new TownHookEnvelope { Json = json, Entrypoint = entrypoint });

    [Fact]
    public void Parse_UserPromptSubmit()
    {
        var e = Parse("""{"hook_event_name":"UserPromptSubmit","session_id":"abc","cwd":"C:\\r\\demo","prompt":"fix the bug"}""");
        Assert.NotNull(e);
        Assert.Equal("UserPromptSubmit", e.Kind);
        Assert.Equal("abc", e.SessionId);
        Assert.Equal(@"C:\r\demo", e.Cwd);
        Assert.Equal("fix the bug", e.Prompt);
    }

    [Fact]
    public void Parse_Stop_TakesLastAssistantMessage()
    {
        var e = Parse("""{"hook_event_name":"Stop","session_id":"abc","cwd":"C:\\r","last_assistant_message":"All done."}""");
        Assert.Equal("All done.", e!.LastReply);
    }

    [Fact]
    public void Parse_PermissionRequest()
    {
        var e = Parse("""
            {"hook_event_name":"PermissionRequest","session_id":"abc","cwd":"C:\\r","tool_name":"Bash",
             "tool_input":{"command":"npm install"},
             "permission_suggestions":[{"type":"addRules","behavior":"allow"}]}
            """);
        Assert.Equal("Bash", e!.ToolName);
        Assert.Equal("""{"command":"npm install"}""", e.ToolInputJson);
        Assert.Equal("""[{"type":"addRules","behavior":"allow"}]""", e.PermissionSuggestionsJson);
    }

    [Theory]
    [InlineData("claude-desktop", "desktop")]
    [InlineData("claude-desktop-3p", "desktop")]
    [InlineData("cli", "terminal")]
    [InlineData(null, "other")]
    [InlineData("sdk-ts", "other")]
    public void Parse_SurfaceFromEntrypoint(string? entrypoint, string surface)
    {
        var e = Parse("""{"hook_event_name":"SessionStart","session_id":"abc","cwd":"C:\\r"}""", entrypoint);
        Assert.Equal(surface, e!.Surface);
    }

    [Theory]
    [InlineData("""{"hook_event_name":"SessionStart","session_id":"a/b","cwd":"C:\\r"}""")]
    [InlineData("""{"hook_event_name":"SessionStart","session_id":"a b","cwd":"C:\\r"}""")]
    [InlineData("""{"hook_event_name":"SessionStart","session_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","cwd":"C:\\r"}""")]
    [InlineData("""{"hook_event_name":"SessionStart","cwd":"C:\\r"}""")]
    public void Parse_RejectsBadSessionId(string json) => Assert.Null(Parse(json));

    [Fact]
    public void Parse_AcceptsSessionIdOf80Chars() =>
        Assert.NotNull(Parse("{\"hook_event_name\":\"SessionStart\",\"session_id\":\"" + new string('a', 80) + "\",\"cwd\":\"C:\\\\r\"}"));

    [Fact]
    public void Parse_StopFailure_TakesErrorText()
    {
        var e = Parse("""{"hook_event_name":"StopFailure","session_id":"abc","cwd":"C:\\r","error":"rate_limit","last_assistant_message":"API Error: Rate limit reached"}""");
        Assert.Equal("StopFailure", e!.Kind);
        Assert.Equal("API Error: Rate limit reached", e.LastReply);
    }

    [Fact]
    public void Parse_UnknownEvent_ReturnsNull() =>
        Assert.Null(Parse("""{"hook_event_name":"PreCompact","session_id":"abc","cwd":"C:\\r"}"""));

    [Fact]
    public void Parse_BadJson_ReturnsNull() => Assert.Null(Parse("{not json"));

    [Fact]
    public void Parse_KeepsEnvelopeFields()
    {
        var e = HookEventParser.Parse(new TownHookEnvelope
        {
            Json = """{"hook_event_name":"SessionStart","session_id":"abc","cwd":"C:\\r"}""",
            Socket = @"\\.\pipe\x", Token = "tok", HostSessionId = "host1",
        });
        Assert.Equal(@"\\.\pipe\x", e!.Socket);
        Assert.Equal("tok", e.Token);
        Assert.Equal("host1", e.HostSessionId);
    }
}
