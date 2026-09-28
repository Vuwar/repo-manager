using System.Net;
using System.Net.Sockets;
using RepoManager.Contracts;
using RepoManager.Core.Ports;
using RepoManager.Core.Processes;

namespace RepoManager.Tests;

[CollectionDefinition("processes", DisableParallelization = true)]
public class ProcessCollection;

[Collection("processes")]
public class SupervisorTests
{
    private static object Services(object services) => new { services };

    [Fact]
    public async Task Start_waits_for_port_then_stop_frees_it_and_leaves_no_orphans()
    {
        var port = TestEnv.FreePort();
        var pidFile = Path.GetTempFileName();
        using var h = new Harness(Services(new { api = Harness.Fake($"--port {port} --delay-listen 0.5 --child --pid-file {pidFile}", new { port }) }));

        var res = await h.Supervisor.StartAsync(h.Id("api"));
        Assert.True(res.Ok, res.Error);
        var r = h.Supervisor.Find(h.Id("api"))!;
        Assert.Equal(ServiceState.Running, r.State);
        Assert.True(PortInspector.IsListening(port));
        Assert.True(h.State.IsDesired(r.Id));
        var pids = await TestEnv.WaitPids(pidFile);

        var stop = await h.Supervisor.StopAsync(r.Id);
        Assert.True(stop.Ok, stop.Error);
        Assert.Equal(ServiceState.Stopped, r.State);
        var owners = PortInspector.OwnersOf(port);
        Assert.True(owners.Count == 0, $"still listening: {string.Join(", ", owners.Select(o => $"{o.Pid} {PortInspector.ProcessName(o.Pid)} alive={TestEnv.ProcessAlive(o.Pid)}"))}; pids {string.Join(",", pids)} alive {string.Join(",", pids.Select(TestEnv.ProcessAlive))}");
        Assert.True(await TestEnv.Eventually(() => pids.All(p => !TestEnv.ProcessAlive(p))));
        Assert.False(h.State.IsDesired(r.Id));
    }

    [Fact]
    public async Task Dependencies_start_first_and_port_variables_resolve()
    {
        var apiPort = TestEnv.FreePort();
        using var h = new Harness(Services(new
        {
            api = Harness.Fake($"--port {apiPort}", new { port = apiPort }),
            web = Harness.Fake("--port-env WEB_PORT --print-env API_URL", new
            {
                autoPort = true,
                dependsOn = new[] { "api" },
                env = new Dictionary<string, string> { ["WEB_PORT"] = "${port:web}", ["API_URL"] = "http://127.0.0.1:${port:api}" },
            }),
        }));

        var res = await h.Supervisor.StartAsync(h.Id("web"));
        Assert.True(res.Ok, res.Error);
        var api = h.Supervisor.Find(h.Id("api"))!;
        var web = h.Supervisor.Find(h.Id("web"))!;
        Assert.Equal(ServiceState.Running, api.State);
        Assert.Equal(ServiceState.Running, web.State);
        Assert.InRange(web.Port!.Value, PortAllocator.RangeStart, PortAllocator.RangeEnd);
        Assert.True(PortInspector.IsListening(web.Port.Value));
        Assert.Contains(web.Log.Query(), l => l.Text == $"API_URL=http://127.0.0.1:{apiPort}");
    }

    [Fact]
    public async Task External_port_owner_is_reported_and_can_be_killed()
    {
        var port = TestEnv.FreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        using var h = new Harness(Services(new { api = Harness.Fake($"--port {port}", new { port }) }));
        try
        {
            var res = await h.Supervisor.StartAsync(h.Id("api"));
            Assert.False(res.Ok);
            Assert.Contains($"PID {Environment.ProcessId}", res.Error);
            Assert.Equal([StartFixes.KillOwner], res.Fixes);
            Assert.Equal(ServiceState.Failed, h.Supervisor.Find(h.Id("api"))!.State);
        }
        finally { listener.Stop(); }

        // An external fake server holding the port gets killed with KillOwner.
        using var external = ManagedProcess.Start(new ProcessSpec
        {
            CommandLine = ManagedProcess.BuildCommandLine(TestEnv.FakeServerExe, ["--port", port.ToString()]),
            WorkingDirectory = Path.GetTempPath(),
            Environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!),
        }, (_, _) => { });
        Assert.True(await TestEnv.Eventually(() => PortInspector.IsListening(port)));
        var killed = await h.Supervisor.StartAsync(h.Id("api"), new StartOptions(KillOwner: true));
        Assert.True(killed.Ok, killed.Error);
    }

    [Fact]
    public async Task Process_listening_on_another_port_is_reported_as_ignoring_its_port()
    {
        var port = TestEnv.FreePort();
        var other = TestEnv.FreePort();
        using var h = new Harness(Services(new { web = Harness.Fake($"--port {other}", new { port, readyTimeoutSeconds = 2 }) }));

        var res = await h.Supervisor.StartAsync(h.Id("web"));
        Assert.False(res.Ok);
        Assert.Contains($"listens on {other}", res.Error);
        Assert.Contains("${port:web}", res.Error);
        Assert.Equal([StartFixes.EditConfig], res.Fixes);
    }

    [Fact]
    public async Task Busy_port_of_a_movable_service_offers_and_supports_a_new_port()
    {
        var port = TestEnv.FreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        using var h = new Harness(Services(new
        {
            api = Harness.Fake("--port-env API_PORT", new { port, env = new Dictionary<string, string> { ["API_PORT"] = "${port:api}" } }),
        }));
        try
        {
            var res = await h.Supervisor.StartAsync(h.Id("api"));
            Assert.False(res.Ok);
            Assert.Equal([StartFixes.KillOwner, StartFixes.NewPort], res.Fixes);

            var moved = await h.Supervisor.StartAsync(h.Id("api"), new StartOptions(NewPort: true));
            Assert.True(moved.Ok, moved.Error);
            var r = h.Supervisor.Find(h.Id("api"))!;
            Assert.NotEqual(port, r.Port);
            Assert.True(PortInspector.IsListening(r.Port!.Value));
            // A main checkout's fixed port moves for this run only.
            Assert.Null(h.State.GetPort(r.InstanceKey, r.Name));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task New_port_is_refused_when_the_command_cannot_receive_it()
    {
        var port = TestEnv.FreePort();
        using var h = new Harness(Services(new { api = Harness.Fake($"--port {port}", new { port }) }));
        var res = await h.Supervisor.StartAsync(h.Id("api"), new StartOptions(NewPort: true));
        Assert.False(res.Ok);
        Assert.Equal([StartFixes.EditConfig], res.Fixes);
    }

    [Fact]
    public async Task Port_used_by_another_managed_service_is_named()
    {
        var port = TestEnv.FreePort();
        using var h = new Harness(Services(new
        {
            a = Harness.Fake($"--port {port}", new { port }),
            b = Harness.Fake($"--port {port}", new { port }),
        }));
        Assert.True((await h.Supervisor.StartAsync(h.Id("a"))).Ok);
        var res = await h.Supervisor.StartAsync(h.Id("b"));
        Assert.False(res.Ok);
        Assert.Contains(h.Id("a"), res.Error);
    }

    [Fact]
    public async Task Crash_is_detected_and_auto_restart_gives_up_after_limit()
    {
        using var h = new Harness(Services(new
        {
            flaky = Harness.Fake("--crash-after 0.8 --exit-code 5", new { autoRestart = true }),
        }));
        var notes = new List<Notification>();
        h.Supervisor.Notified += n => { lock (notes) notes.Add(n); };

        var res = await h.Supervisor.StartAsync(h.Id("flaky"));
        Assert.True(res.Ok, res.Error);
        var r = h.Supervisor.Find(h.Id("flaky"))!;

        Assert.True(await TestEnv.Eventually(() => r.State == ServiceState.Failed, 30000), $"state {r.State}");
        Assert.Contains("giving up", r.LastError);
        Assert.True(r.RestartCount >= 4);
        lock (notes) Assert.Contains(notes, n => n.Title == "Service failed");
    }

    [Fact]
    public async Task Crash_without_auto_restart_stays_crashed_and_desired()
    {
        using var h = new Harness(Services(new { once = Harness.Fake("--crash-after 0.8 --exit-code 9") }));
        Assert.True((await h.Supervisor.StartAsync(h.Id("once"))).Ok);
        var r = h.Supervisor.Find(h.Id("once"))!;
        Assert.True(await TestEnv.Eventually(() => r.State == ServiceState.Crashed));
        Assert.Equal(9, r.LastExitCode);
        Assert.True(h.State.IsDesired(r.Id)); // a crash does not change what the user wants
    }

    [Fact]
    public async Task Exit_before_ready_fails_with_log_tail()
    {
        var port = TestEnv.FreePort();
        using var h = new Harness(Services(new { api = Harness.Fake($"--port {port} --delay-listen 5 --crash-after 0.2 --exit-code 2", new { port }) }));
        var res = await h.Supervisor.StartAsync(h.Id("api"));
        Assert.False(res.Ok);
        Assert.Contains("exited with code 2", res.Error);
        Assert.Contains(res.LogTail, l => l.Contains("crashing"));
    }

    [Fact]
    public async Task Health_check_marks_unhealthy_and_recovers()
    {
        var port = TestEnv.FreePort();
        var flag = Path.Combine(Path.GetTempPath(), "rm-unhealthy-" + Guid.NewGuid().ToString("N")[..6]);
        using var h = new Harness(Services(new
        {
            api = Harness.Fake($"--port {port} --unhealthy-file {flag}", new { port, health = $"http://127.0.0.1:{port}/health" }),
        }));
        Assert.True((await h.Supervisor.StartAsync(h.Id("api"))).Ok);
        var r = h.Supervisor.Find(h.Id("api"))!;
        var notes = new List<Notification>();
        h.Supervisor.Notified += n => { lock (notes) notes.Add(n); };

        File.WriteAllText(flag, "x");
        Assert.True(await TestEnv.Eventually(() => r.State == ServiceState.Unhealthy));
        Assert.True(await TestEnv.Eventually(() => { lock (notes) return notes.Any(n => n.Title == "Service unhealthy"); }));
        File.Delete(flag);
        Assert.True(await TestEnv.Eventually(() => r.State == ServiceState.Running));
    }

    [Fact]
    public async Task Prepare_steps_run_when_condition_holds()
    {
        using var h = new Harness(Services(new
        {
            svc = Harness.Fake("", new
            {
                prepare = new object[]
                {
                    new { @if = "!exists(marker.txt)", run = "echo prepared> marker.txt" },
                    new { @if = "!exists(marker.txt)", run = "exit 1" },
                },
            }),
        }));
        var res = await h.Supervisor.StartAsync(h.Id("svc"));
        Assert.True(res.Ok, res.Error);
        Assert.True(File.Exists(Path.Combine(h.ProjectDir, "marker.txt")));
    }

    [Fact]
    public async Task Failing_prepare_step_fails_the_start()
    {
        using var h = new Harness(Services(new { svc = Harness.Fake("", new { prepare = new object[] { new { run = "exit 3" } } }) }));
        var res = await h.Supervisor.StartAsync(h.Id("svc"));
        Assert.False(res.Ok);
        Assert.Contains("exited with code 3", res.Error);
    }

    [Fact]
    public async Task Restore_starts_previously_desired_services()
    {
        var port = TestEnv.FreePort();
        var services = Services(new { api = Harness.Fake($"--port {port}", new { port }) });
        string dataDir, projectDir;
        using (var h = new Harness(services))
        {
            Assert.True((await h.Supervisor.StartAsync(h.Id("api"))).Ok);
            dataDir = h.DataDir; projectDir = h.ProjectDir;
            await h.Supervisor.ShutdownAsync(); // daemon quits: desired state is kept
            Assert.True(h.State.IsDesired(h.Id("api")));
            h.Supervisor.Dispose();
            // Keep the folders: a second harness reuses them.
            var h2 = new Harness(services, dataDir: dataDir, projectDir: projectDir);
            try
            {
                await h2.Supervisor.RestoreAsync();
                Assert.Equal(ServiceState.Running, h2.Supervisor.Find(h2.Id("api"))!.State);
            }
            finally
            {
                await h2.Supervisor.ShutdownAsync();
                h2.Supervisor.Dispose();
            }
        }
    }

    [Fact]
    public async Task Tasks_run_to_exit_and_report_code()
    {
        using var h = new Harness(new
        {
            services = new { },
            tasks = new { hello = new { command = "echo", args = new[] { "hi" } }, bad = new { command = "exit", args = new[] { "4" } } },
        });
        Assert.True((await h.Supervisor.RunTaskAsync(h.Id("hello"))).Ok);
        var done = await h.Supervisor.WaitTaskAsync(h.Id("hello"), TimeSpan.FromSeconds(10));
        Assert.True(done.Ok, done.Error);
        Assert.Contains(done.LogTail, l => l == "hi");

        await h.Supervisor.RunTaskAsync(h.Id("bad"));
        var bad = await h.Supervisor.WaitTaskAsync(h.Id("bad"), TimeSpan.FromSeconds(10));
        Assert.False(bad.Ok);
        Assert.Equal(4, h.Supervisor.FindTask(h.Id("bad"))!.LastExitCode);
    }

    [Fact]
    public async Task Resolve_short_names_from_cwd()
    {
        using var h = new Harness(Services(new { api = Harness.Fake(""), web = Harness.Fake("") }));
        var sub = Directory.CreateDirectory(Path.Combine(h.ProjectDir, "web", "src")).FullName;
        var r = h.Supervisor.Resolve("api", sub);
        Assert.Null(r.Error);
        Assert.Equal([h.Id("api")], r.ServiceIds);
        var all = h.Supervisor.Resolve("all", h.ProjectDir);
        Assert.Equal(2, all.ServiceIds.Count);
        Assert.NotNull(h.Supervisor.Resolve("nope", h.ProjectDir).Error);
        Assert.Equal([h.Id("web")], h.Supervisor.Resolve(h.Id("web"), null).ServiceIds);
        await Task.CompletedTask;
    }
}
