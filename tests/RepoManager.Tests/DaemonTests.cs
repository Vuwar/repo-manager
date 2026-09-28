using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RepoManager.App.Daemon;
using RepoManager.Contracts;
using RepoManager.Core.Config;

namespace RepoManager.Tests;

/// <summary>A real daemon (HTTP API on a random port) over a temp data dir with one fake project.</summary>
public sealed class DaemonFixture : IAsyncLifetime
{
    public string Home { get; } = Path.Combine(Path.GetTempPath(), "rm-tests", "d-" + Guid.NewGuid().ToString("N")[..8]);
    public string Project => Path.Combine(Home, "demo");
    public int ApiPort { get; } = TestEnv.FreePort();
    public DaemonHost Host { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Project);
        File.WriteAllText(Path.Combine(Project, "devservers.json"), JsonSerializer.Serialize(new
        {
            services = new
            {
                api = Harness.Fake("--port ${port:api} --lines 3", new { port = ApiPort, health = "http://127.0.0.1:${port:api}/" }),
                web = Harness.Fake("--port-env WEB_PORT", new { autoPort = true, dependsOn = new[] { "api" }, env = new Dictionary<string, string> { ["WEB_PORT"] = "${port:web}" } }),
                broken = Harness.Fake("--crash-after 0.1 --exit-code 4", new { port = TestEnv.FreePort() }),
            },
            tasks = new { hello = new { command = "echo", args = new[] { "hi" } } },
        }, Protocol.Json));
        File.WriteAllText(Path.Combine(Home, "registry.json"),
            JsonSerializer.Serialize(new RegistryFile { Projects = [new RegistryProject { Path = Project }] }, Protocol.Json));

        Host = new DaemonHost(Home, 0, watchConfig: false, Harness.FastOptions);
        await Host.StartAsync();
        Http = Client(Host.Token);
    }

    public HttpClient Client(string? token)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Host.Port}/") };
        if (token != null) http.DefaultRequestHeaders.Add(Protocol.TokenHeader, token);
        return http;
    }

    public async Task<ActionResultDto> Post(string path, object body)
    {
        var resp = await Http.PostAsJsonAsync(path, body, Protocol.Json);
        return (await resp.Content.ReadFromJsonAsync<ActionResultDto>(Protocol.Json))!;
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        await Host.DisposeAsync();
        try { Directory.Delete(Home, true); } catch { }
    }
}

[CollectionDefinition("daemon", DisableParallelization = true)]
public class DaemonCollection : ICollectionFixture<DaemonFixture>;

[Collection("daemon")]
public class ApiTests(DaemonFixture f)
{
    [Fact]
    public async Task Api_requires_the_token()
    {
        using var anon = f.Client(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("api/ping")).StatusCode);
        using var wrong = f.Client("nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Http.GetAsync("api/ping")).StatusCode);
    }

    [Fact]
    public async Task Foreign_host_header_is_rejected()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/ping");
        req.Headers.Host = "evil.example.com";
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Start_with_dependency_then_projects_logs_and_stop()
    {
        var res = await f.Post("api/services/start", new ServiceActionRequest { Target = "web", Cwd = f.Project });
        Assert.True(res.Ok, res.Error);

        var projects = await f.Http.GetFromJsonAsync<List<ProjectDto>>("api/projects", Protocol.Json);
        var services = projects!.Single().Instances.Single().Services;
        Assert.Equal(ServiceState.Running, services.Single(s => s.Name == "api").State);
        Assert.Equal(ServiceState.Running, services.Single(s => s.Name == "web").State);

        var logs = await f.Http.GetFromJsonAsync<List<LogLineDto>>($"api/logs?target=demo/api&tail=50", Protocol.Json);
        Assert.Contains(logs!, l => l.Text == "line 1");
        Assert.Contains(logs!, l => l.Text == "line 2 (stderr)" && l.Stream == LogStream.Err);

        var stop = await f.Post("api/services/stop", new ServiceActionRequest { Target = "all", Cwd = f.Project });
        Assert.True(stop.Ok, stop.Error);
    }

    [Fact]
    public async Task Failed_start_returns_reason_and_log_tail()
    {
        var res = await f.Post("api/services/start", new ServiceActionRequest { Target = "demo/broken" });
        Assert.False(res.Ok);
        Assert.Contains("exited with code 4", res.Error);
        Assert.Contains(res.LogTail, l => l.Contains("crashing"));
    }

    [Fact]
    public async Task Events_stream_status_changes()
    {
        using var http = f.Client(f.Host.Token);
        http.Timeout = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var resp = await http.GetAsync("api/events?logs=demo/api", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
        Assert.StartsWith(": connected", await reader.ReadLineAsync(cts.Token));

        var start = f.Post("api/services/start", new ServiceActionRequest { Target = "demo/api" });
        bool sawRunning = false, sawLog = false;
        while (!(sawRunning && sawLog))
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null) break;
            if (!line.StartsWith("data: ")) continue;
            var e = JsonSerializer.Deserialize<EventDto>(line[6..], Protocol.Json)!;
            var payload = (JsonElement)e.Payload!;
            if (e.Type == "status" && payload.GetProperty("state").GetString() == "running") sawRunning = true;
            if (e.Type == "log" && payload.GetProperty("source").GetString() == "demo/api") sawLog = true;
        }
        Assert.True((await start).Ok);
        Assert.True(sawRunning && sawLog);
        await f.Post("api/services/stop", new ServiceActionRequest { Target = "demo/api" });
    }

    [Fact]
    public async Task Hook_endpoint_denies_killing_a_running_service()
    {
        Assert.True((await f.Post("api/services/start", new ServiceActionRequest { Target = "demo/api" })).Ok);
        try
        {
            var resp = await f.Http.PostAsJsonAsync("api/hook", new HookRequest
            {
                ToolName = "Bash",
                ToolInputJson = JsonSerializer.Serialize(new { command = $"npx kill-port {f.ApiPort}" }),
                Cwd = f.Project,
            }, Protocol.Json);
            var d = (await resp.Content.ReadFromJsonAsync<HookDecisionDto>(Protocol.Json))!;
            Assert.True(d.Deny);
            Assert.Contains("demo/api", d.Reason);
        }
        finally
        {
            await f.Post("api/services/stop", new ServiceActionRequest { Target = "demo/api" });
        }
    }

    [Fact]
    public async Task Config_view_shows_origins()
    {
        var cfg = await f.Http.GetFromJsonAsync<EffectiveConfigDto>("api/config?instance=demo", Protocol.Json);
        var api = cfg!.Services.Single(s => s.Name == "api");
        Assert.Equal(Layers.DevServers, api.Fields.Single(x => x.Field == "port").Origin);
        Assert.Equal(Layers.Default, api.Fields.Single(x => x.Field == "cwd").Origin);
    }

    [Fact]
    public async Task Tasks_run_and_wait()
    {
        var resp = await f.Http.PostAsJsonAsync("api/tasks/run?wait=true&timeout=20", new TaskRunRequest { Target = "hello", Cwd = f.Project }, Protocol.Json);
        var res = (await resp.Content.ReadFromJsonAsync<ActionResultDto>(Protocol.Json))!;
        Assert.True(res.Ok, res.Error);
        Assert.Contains("hi", res.LogTail);
    }
}

/// <summary>Runs the real devm.exe against the fixture daemon.</summary>
[Collection("daemon")]
public class CliTests(DaemonFixture f)
{
    public static string DevmExe
    {
        get
        {
            var config = AppContext.BaseDirectory.Contains("\\Release\\") ? "Release" : "Debug";
            return Path.Combine(TestEnv.RepoRoot, "src", "RepoManager.Cli", "bin", config, "net10.0", "devm.exe");
        }
    }

    private async Task<(int Code, string Out, string Err)> Devm(string args, string? stdin = null, string? cwd = null)
    {
        var psi = new ProcessStartInfo(DevmExe, args)
        {
            WorkingDirectory = cwd ?? f.Project,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.Environment["REPOMANAGER_HOME"] = f.Home;
        psi.Environment["REPOMANAGER_EXE"] = "C:\\does-not-exist.exe"; // never spawn a real app from tests
        psi.Environment["REPOMANAGER_HOOK_DEBUG"] = "1";
        using var p = Process.Start(psi)!;
        if (stdin != null) { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); }
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (p.ExitCode, await o, await e);
    }

    [Fact]
    public async Task Start_status_url_stop()
    {
        var start = await Devm("start api");
        Assert.True(start.Code == 0, start.Err);
        Assert.Contains("demo/api running", start.Out);

        var status = await Devm("status");
        Assert.Contains("demo/api", status.Out);
        Assert.Contains("running", status.Out);

        var url = await Devm("url api");
        Assert.Equal($"http://localhost:{f.ApiPort}", url.Out.Trim());

        var json = await Devm("status --json");
        Assert.Contains("\"state\": \"running\"", json.Out);

        var stop = await Devm("stop api");
        Assert.Equal(0, stop.Code);
    }

    [Fact]
    public async Task Failed_start_exits_2_with_log_tail()
    {
        var r = await Devm("start broken");
        Assert.Equal(2, r.Code);
        Assert.Contains("last log lines", r.Err);
    }

    [Fact]
    public async Task Unknown_service_is_an_error()
    {
        var r = await Devm("start nope");
        Assert.Equal(1, r.Code);
        Assert.Contains("unknown service", r.Err);
    }

    [Fact]
    public async Task Hook_prints_deny_json_and_allows_other_commands()
    {
        Assert.Equal(0, (await Devm("start api")).Code);
        try
        {
            var input = JsonSerializer.Serialize(new { tool_name = "PowerShell", tool_input = new { command = $"npx kill-port {f.ApiPort}" }, cwd = f.Project });
            var deny = await Devm("hook", "\uFEFF" + input);
            Assert.True(deny.Code == 0 && deny.Out.Length > 0, "out: " + deny.Out + " err: " + deny.Err);
            using var doc = JsonDocument.Parse(deny.Out);
            Assert.Equal("deny", doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());

            var allow = await Devm("hook", JsonSerializer.Serialize(new { tool_name = "Bash", tool_input = new { command = "git status" }, cwd = f.Project }));
            Assert.Equal(0, allow.Code);
            Assert.Equal("", allow.Out.Trim());
        }
        finally { await Devm("stop api"); }
    }

    [Fact]
    public async Task Hook_allows_everything_when_daemon_is_unreachable()
    {
        var psi = new ProcessStartInfo(DevmExe, "hook") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.Environment["REPOMANAGER_HOME"] = Path.Combine(Path.GetTempPath(), "rm-tests", "empty-" + Guid.NewGuid().ToString("N")[..6]);
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync(JsonSerializer.Serialize(new { tool_name = "Bash", tool_input = new { command = "taskkill /F /IM node.exe" }, cwd = "C:\\" }));
        p.StandardInput.Close();
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.Equal(0, p.ExitCode);
        Assert.Equal("", output.Trim());
    }

    [Fact]
    public async Task Mcp_server_lists_tools_and_starts_a_service()
    {
        var psi = new ProcessStartInfo(DevmExe, "mcp")
        {
            WorkingDirectory = f.Project,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["REPOMANAGER_HOME"] = f.Home;
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        var id = 0;

        async Task<JsonElement> Call(string method, object? @params)
        {
            var myId = ++id;
            await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = myId, method, @params }));
            await p.StandardInput.FlushAsync();
            while (true)
            {
                var line = await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.NotNull(line);
                using var doc = JsonDocument.Parse(line!);
                if (doc.RootElement.TryGetProperty("id", out var rid) && rid.GetInt32() == myId)
                    return doc.RootElement.Clone();
            }
        }

        try
        {
            await Call("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
            await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));

            var tools = await Call("tools/list", new { });
            var names = tools.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            Assert.Equal(["get_logs", "get_url", "list_services", "restart_service", "run_task", "service_status", "start_service", "stop_service"], names.Order().ToList());

            var start = await Call("tools/call", new { name = "start_service", arguments = new { service = "api" } });
            var text = start.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
            Assert.StartsWith("OK: demo/api running", text);

            var logs = await Call("tools/call", new { name = "get_logs", arguments = new { service = "api", tail = 20 } });
            Assert.Contains("line 1", logs.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

            var stop = await Call("tools/call", new { name = "stop_service", arguments = new { service = "api" } });
            Assert.StartsWith("OK:", stop.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
