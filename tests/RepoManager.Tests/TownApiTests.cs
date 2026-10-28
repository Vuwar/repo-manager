using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
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
            var s = await Session(id);
            Assert.Empty(s!.Prompts);
            Assert.Equal("working", s.State);
        }
        finally
        {
            await Focus(false);
            await PostHook(HookJson("SessionEnd", id));
        }
    }

    [Fact]
    public async Task Permission_Unfocused_ThenNotification_StillListed()
    {
        var id = NewId();
        await Focus(false);
        await PostPermission(id);
        // Claude Code shows its dialog and reports it; the town keeps "Answer in the window".
        await PostHook(HookJson("Notification", id, extra: "\"notification_type\":\"permission_prompt\",\"message\":\"Claude needs your permission to use Bash\""));
        var p = Assert.Single((await Session(id))!.Prompts);
        Assert.Equal("npm install", p.Summary);
        await PostHook(HookJson("SessionEnd", id));
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
        // Claude Code shows inbox messages as coming from another session; the first line says it is the user.
        var content = JsonDocument.Parse((await received).Split('\n')[1]).RootElement.GetProperty("message").GetProperty("content").GetString()!;
        Assert.StartsWith("[From the user, typed in RepoManager Agent Town", content);
        Assert.EndsWith("\n\nhello", content);
        await PostHook(HookJson("SessionEnd", id));
    }

    [Theory]
    [InlineData("not-a-host-id")]
    [InlineData("local_a&x=1")]
    [InlineData("")]
    public async Task OpenHost_RejectsAnythingButADesktopSessionId(string host) =>
        Assert.False((await f.Post("api/town/open-host", new TownOpenHostRequest(host))).Ok);

    [Fact]
    public async Task Open_UnknownSession_Fails() =>
        Assert.False((await f.Post("api/town/open", new TownOpenRequest(NewId()))).Ok);

    [Fact]
    public async Task Open_NoPid_FailsWithReadableMessage()
    {
        var id = NewId();
        // A terminal session (no desktop link) that never reported its process.
        await f.Post("api/town/hook", new TownHookEnvelope { Json = HookJson("SessionStart", id), Entrypoint = "cli" });
        var r = await f.Post("api/town/open", new TownOpenRequest(id));
        Assert.False(r.Ok);
        Assert.Contains("window", r.Error ?? r.Message);
        Assert.DoesNotContain("next event", r.Error ?? r.Message);
        await PostHook(HookJson("SessionEnd", id));
    }

    [Fact]
    public async Task Open_ProcessGone_NeverActivatesAReusedPid()
    {
        var id = NewId();
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        await f.Post("api/town/hook", new TownHookEnvelope { Json = HookJson("SessionStart", id), Entrypoint = "cli", ClaudePid = child.Id });
        await child.WaitForExitAsync();
        var r = await f.Post("api/town/open", new TownOpenRequest(id));
        Assert.False(r.Ok);
        Assert.Contains("no longer running", r.Error ?? r.Message);
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

    // ---- devm town-hook (the real devm.exe against the fixture daemon) ----

    private async Task<(int Code, string Out, TimeSpan Took)> TownHook(byte[] stdin, string? home = null, string entrypoint = "cli")
    {
        var psi = new ProcessStartInfo(CliTests.DevmExe, "town-hook")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.Environment["REPOMANAGER_HOME"] = home ?? f.Home;
        psi.Environment["REPOMANAGER_EXE"] = "C:\\does-not-exist.exe"; // never spawn a real app from tests
        psi.Environment["CLAUDE_CODE_ENTRYPOINT"] = entrypoint;
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        await p.StandardInput.BaseStream.WriteAsync(stdin);
        p.StandardInput.Close();
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (p.ExitCode, output, sw.Elapsed);
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task TownHook_StatusEvent_PostsAndExitsZero()
    {
        var id = NewId();
        var json = HookJson("Stop", id, extra: "\"last_assistant_message\":\"Done here.\"");
        var (code, output, _) = await TownHook([.. Encoding.UTF8.GetPreamble(), .. Utf8(json)]);
        Assert.Equal(0, code);
        Assert.Equal("", output);
        var s = await Session(id);
        Assert.Equal("done", s?.State);
        Assert.Equal("terminal", s?.Surface);
        await PostHook(HookJson("SessionEnd", id));
    }

    [Fact]
    public async Task TownHook_NoDaemon_ExitsZeroFast()
    {
        var home = Path.Combine(Path.GetTempPath(), "rm-tests", "empty-" + Guid.NewGuid().ToString("N")[..6]);
        var (code, output, took) = await TownHook(Utf8(HookJson("Stop", NewId())), home);
        Assert.Equal(0, code);
        Assert.Equal("", output);
        Assert.True(took < TimeSpan.FromSeconds(1.5), took.ToString());
        Assert.False(File.Exists(Protocol.DaemonInfoPath(home)));
    }

    [Fact]
    public async Task TownHook_Permission_PrintsDecision()
    {
        var id = NewId();
        await Focus(true);
        try
        {
            var json = HookJson("PermissionRequest", id, extra:
                "\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"npm test\"},"
                + "\"permission_suggestions\":[{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Bash\",\"ruleContent\":\"npm test\"}],\"behavior\":\"allow\",\"destination\":\"localSettings\"}]");
            var run = TownHook(Utf8(json));
            var p = await WaitForPrompt(id);
            Assert.True((await f.Post("api/town/permission/answer", new PermissionAnswerRequest(p.RequestId, "allow", true))).Ok);
            var (code, output, _) = await run;
            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(output);
            var hso = doc.RootElement.GetProperty("hookSpecificOutput");
            Assert.Equal("PermissionRequest", hso.GetProperty("hookEventName").GetString());
            var decision = hso.GetProperty("decision");
            Assert.Equal("allow", decision.GetProperty("behavior").GetString());
            var update = Assert.Single(decision.GetProperty("updatedPermissions").EnumerateArray());
            Assert.Equal("addRules", update.GetProperty("type").GetString());
            // "Always OK" is for this session only, never a rule written to disk.
            Assert.Equal("session", update.GetProperty("destination").GetString());
        }
        finally
        {
            await Focus(false);
            await PostHook(HookJson("SessionEnd", id));
        }
    }

    [Fact]
    public async Task TownHook_Permission_Deny_PrintsDenyWithMessage()
    {
        var id = NewId();
        await Focus(true);
        try
        {
            var run = TownHook(Utf8(HookJson("PermissionRequest", id, extra: "\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"rm x\"}")));
            var p = await WaitForPrompt(id);
            await f.Post("api/town/permission/answer", new PermissionAnswerRequest(p.RequestId, "deny", false));
            var (code, output, _) = await run;
            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(output);
            var decision = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
            Assert.Equal("deny", decision.GetProperty("behavior").GetString());
            Assert.False(string.IsNullOrEmpty(decision.GetProperty("message").GetString()));
        }
        finally
        {
            await Focus(false);
            await PostHook(HookJson("SessionEnd", id));
        }
    }

    [Fact]
    public async Task TownHook_Permission_NoDecision_PrintsNothing()
    {
        var id = NewId();
        await Focus(false);
        var (code, output, _) = await TownHook(Utf8(HookJson("PermissionRequest", id, extra: "\"tool_name\":\"Bash\"")));
        Assert.Equal(0, code);
        Assert.Equal("", output);
        await PostHook(HookJson("SessionEnd", id));
    }
}
