using System.Net.Http.Json;
using System.Text.Json;
using RepoManager.Contracts;

namespace RepoManager.Tests;

[Collection("daemon")]
public class TownApiTests(DaemonFixture f)
{
    private static string NewId() => "t-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Hook stdin JSON; extra is raw JSON members appended to the object.</summary>
    private string HookJson(string kind, string id, string? cwd = null, string extra = "")
    {
        var json = JsonSerializer.Serialize(new { hook_event_name = kind, session_id = id, cwd = cwd ?? f.Project });
        return extra.Length == 0 ? json : json[..^1] + "," + extra + "}";
    }

    private Task<ActionResultDto> PostHook(string json, string? socket = null, string? token = null) =>
        f.Post("api/town/hook", new TownHookEnvelope { Json = json, Socket = socket, Token = token, Entrypoint = "claude-desktop" });

    private async Task<PermissionDecisionDto> PostPermission(string id)
    {
        var json = HookJson("PermissionRequest", id, extra: "\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"npm install\"}");
        var resp = await f.Http.PostAsJsonAsync("api/town/permission", new TownHookEnvelope { Json = json }, Protocol.Json);
        return (await resp.Content.ReadFromJsonAsync<PermissionDecisionDto>(Protocol.Json))!;
    }

    private async Task<AgentSessionDto?> Session(string id) =>
        (await f.Http.GetFromJsonAsync<List<AgentSessionDto>>("api/town/sessions", Protocol.Json))!.FirstOrDefault(s => s.Id == id);

    private async Task<PermissionPromptDto> WaitForPrompt(string id)
    {
        for (var i = 0; i < 100; i++)
        {
            if ((await Session(id))?.Prompts is [var p, ..]) return p;
            await Task.Delay(50);
        }
        throw new TimeoutException("no prompt listed");
    }

    private Task Focus(bool focused) => f.Post("api/town/focus", new TownFocusRequest(focused));

    [Fact]
    public async Task Hook_SessionStart_ListedAndStreamed()
    {
        var id = NewId();
        using var http = f.Client(f.Host.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var resp = await http.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
        Assert.StartsWith(": connected", await reader.ReadLineAsync(cts.Token));

        Assert.True((await PostHook(HookJson("SessionStart", id))).Ok);

        var sawAgent = false;
        while (!sawAgent)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null) break;
            if (!line.StartsWith("data: ")) continue;
            var e = JsonSerializer.Deserialize<EventDto>(line[6..], Protocol.Json)!;
            if (e.Type == "agent" && ((JsonElement)e.Payload!).GetProperty("id").GetString() == id) sawAgent = true;
        }
        Assert.True(sawAgent);
        var s = await Session(id);
        Assert.Equal("demo", s?.Project);
        Assert.Equal("desktop", s?.Surface);
        await PostHook(HookJson("SessionEnd", id));
    }

    [Fact]
    public async Task Hook_SessionEnd_StreamsAgentEnd()
    {
        var id = NewId();
        await PostHook(HookJson("SessionStart", id));
        using var http = f.Client(f.Host.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var resp = await http.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
        await reader.ReadLineAsync(cts.Token);

        await PostHook(HookJson("SessionEnd", id));
        string? line;
        do line = await reader.ReadLineAsync(cts.Token);
        while (line != null && !(line.Contains("\"agentEnd\"") && line.Contains(id)));
        Assert.NotNull(line);
        Assert.Null(await Session(id));
    }

    [Fact]
    public async Task Hook_BadJson_Returns400OrFail()
    {
        var r = await PostHook("{not json");
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Hook_NonPermissionEvent_ClearsUnanswerablePrompts()
    {
        var id = NewId();
        await Focus(false);
        await PostPermission(id);
        Assert.Single((await Session(id))!.Prompts);
        await PostHook(HookJson("PostToolUse", id, extra: "\"tool_name\":\"Bash\""));
        Assert.Empty((await Session(id))!.Prompts);
        await PostHook(HookJson("SessionEnd", id));
    }

    [Fact]
    public async Task Permission_Unfocused_NoDecision()
    {
        var id = NewId();
        await Focus(false);
        var t = PostPermission(id);
        Assert.Same(t, await Task.WhenAny(t, Task.Delay(3000)));
        Assert.Null((await t).Behavior);
        var s = await Session(id);
        var p = Assert.Single(s!.Prompts);
        Assert.False(p.Answerable);
        Assert.Equal("npm install", p.Summary);
        Assert.Equal("waiting", s.State);
        await PostHook(HookJson("SessionEnd", id));
    }

    [Fact]
    public async Task Permission_Focused_AnsweredFromTown()
    {
        var id = NewId();
        await Focus(true);
        try
        {
            var t = PostPermission(id);
            var p = await WaitForPrompt(id);
            Assert.True(p.Answerable);
            Assert.True((await f.Post("api/town/permission/answer", new PermissionAnswerRequest(p.RequestId, "allow", false))).Ok);
            Assert.Equal("allow", (await t.WaitAsync(TimeSpan.FromSeconds(5))).Behavior);
        }
        finally
        {
            await Focus(false);
            await PostHook(HookJson("SessionEnd", id));
        }
    }

    [Fact]
    public async Task Permission_Answer_UnknownId_Fails() =>
        Assert.False((await f.Post("api/town/permission/answer", new PermissionAnswerRequest("nope", "allow", false))).Ok);

    [Fact]
    public async Task Permission_FocusLost_Releases()
    {
        var id = NewId();
        await Focus(true);
        var t = PostPermission(id);
        await WaitForPrompt(id);
        await Focus(false);
        Assert.Null((await t.WaitAsync(TimeSpan.FromSeconds(5))).Behavior);
        await PostHook(HookJson("SessionEnd", id));
    }

    [Fact]
    public async Task Send_UnknownSession_Fails()
    {
        var r = await f.Post("api/town/send", new TownSendRequest(NewId(), "hi"));
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Send_ToFakeInbox_Succeeds()
    {
        var id = NewId();
        var (path, received) = AgentInboxTests.FakeInbox();
        await PostHook(HookJson("SessionStart", id), path, "tok-123");
        Assert.True((await Session(id))!.CanSend);
        var r = await f.Post("api/town/send", new TownSendRequest(id, "hello"));
        Assert.True(r.Ok, r.Message);
        Assert.Contains("\"content\":\"hello\"", await received);
        await PostHook(HookJson("SessionEnd", id));
    }

    [Fact]
    public async Task Sessions_NeverContainToken()
    {
        var id = NewId();
        await PostHook(HookJson("SessionStart", id), @"\\.\pipe\x", "very-secret-token-42");
        var raw = await f.Http.GetStringAsync("api/town/sessions");
        Assert.Contains(id, raw);
        Assert.DoesNotContain("very-secret-token-42", raw);
        await PostHook(HookJson("SessionEnd", id));
    }
}
