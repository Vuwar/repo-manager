using System.Diagnostics;
using System.Text.Json;
using RepoManager.Contracts;
using RepoManager.Core.Config;
using RepoManager.Core.Git;
using RepoManager.Core.Logs;
using RepoManager.Core.Ports;
using RepoManager.Core.Processes;
using RepoManager.Core.State;

namespace RepoManager.Tests;

[Collection("processes")]
public class WorktreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rm-tests", "wt-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _repo, _worktree, _data;
    private readonly int _port = TestEnv.FreePort();

    public WorktreeTests()
    {
        _repo = Path.Combine(_root, "shop");
        _worktree = Path.Combine(_root, "shop-feat");
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_data);
        File.WriteAllText(Path.Combine(_repo, "devservers.json"), JsonSerializer.Serialize(new
        {
            services = new
            {
                api = Harness.Fake("--port ${port:api}", new { port = _port }),
                legacy = Harness.Fake($"--port {_port + 1}", new { port = _port + 1 }),
            },
        }, Protocol.Json));
        Git(_repo, "init", "-q", "-b", "main");
        Git(_repo, "-c", "user.email=t@t", "-c", "user.name=t", "add", ".");
        Git(_repo, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "init");
        Git(_repo, "worktree", "add", "-q", "-b", "feat", _worktree);
        File.WriteAllText(Path.Combine(_data, "registry.json"),
            JsonSerializer.Serialize(new RegistryFile { Projects = [new RegistryProject { Path = _repo }] }, Protocol.Json));
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
    }

    [Fact]
    public async Task Worktree_gets_its_own_instance_ports_and_readiness_check()
    {
        using var config = new ConfigService(_data, new GitService(), watch: false);
        var state = new StateStore(_data);
        using var logs = new LogStore(null);
        using var sup = new Supervisor(config, state, logs, null, Harness.FastOptions);
        try
        {
            var keys = config.Instances.Select(i => i.InstanceKey).ToList();
            Assert.Equal(["shop", "shop@shop-feat"], keys);

            // Short names resolve to the checkout that contains the cwd.
            var fromWorktree = sup.Resolve("api", Path.Combine(_worktree));
            Assert.Equal(["shop@shop-feat/api"], fromWorktree.ServiceIds);
            // A full id is explicit: shop/api is the main checkout even from inside the worktree.
            Assert.Equal(["shop/api"], sup.Resolve("shop/api", _worktree).ServiceIds);

            Assert.True((await sup.StartAsync("shop/api")).Ok);
            var wt = await sup.StartAsync("shop@shop-feat/api");
            Assert.True(wt.Ok, wt.Error);
            var main = sup.Find("shop/api")!;
            var copy = sup.Find("shop@shop-feat/api")!;
            Assert.Equal(_port, main.Port);
            Assert.InRange(copy.Port!.Value, PortAllocator.RangeStart, PortAllocator.RangeEnd);
            Assert.True(PortInspector.IsListening(copy.Port.Value));
            var sticky = copy.Port;

            // Restart keeps the sticky port.
            Assert.True((await sup.RestartAsync(copy.Id)).Ok);
            Assert.Equal(sticky, copy.Port);

            // Fixed port without ${port:...}: refused in a worktree unless forced.
            var legacy = await sup.StartAsync("shop@shop-feat/legacy");
            Assert.False(legacy.Ok);
            Assert.Contains("not worktree-ready", legacy.Error);
            Assert.False(sup.Find("shop@shop-feat/legacy")!.WorktreeReady);
            Assert.True(sup.Find("shop@shop-feat/api")!.WorktreeReady);
        }
        finally
        {
            await sup.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Removed_worktree_disappears_and_its_services_stop()
    {
        using var config = new ConfigService(_data, new GitService(), watch: false);
        var state = new StateStore(_data);
        using var logs = new LogStore(null);
        using var sup = new Supervisor(config, state, logs, null, Harness.FastOptions);
        try
        {
            Assert.True((await sup.StartAsync("shop@shop-feat/api")).Ok);
            var r = sup.Find("shop@shop-feat/api")!;
            var pid = r.Process!.Pid;

            // Windows cannot delete a folder a running process sits in, so drop git's record of the worktree instead.
            Directory.Delete(Path.Combine(_repo, ".git", "worktrees", "shop-feat"), true);
            config.RefreshWorktrees();

            Assert.DoesNotContain(config.Instances, i => i.Worktree != null);
            Assert.Null(sup.Find("shop@shop-feat/api"));
            Assert.True(await TestEnv.Eventually(() => !TestEnv.ProcessAlive(pid)));
        }
        finally
        {
            await sup.ShutdownAsync();
        }
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch { }
    }
}
