using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using RepoManager.Contracts;
using RepoManager.Core.Ports;
using RepoManager.Core.Processes;

namespace RepoManager.Tests;

[Collection("processes")]
public class ManagedProcessTests
{
    private static ProcessSpec Spec(string args, Dictionary<string, string>? env = null) => new()
    {
        CommandLine = ManagedProcess.BuildCommandLine(TestEnv.FakeServerExe, args.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
        WorkingDirectory = Path.GetTempPath(),
        Environment = env ?? Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public async Task Captures_stdout_and_stderr_lines()
    {
        var lines = new ConcurrentQueue<(LogStream, string)>();
        using var p = ManagedProcess.Start(Spec("--lines 4 --crash-after 0.3 --exit-code 7"), (s, l) => lines.Enqueue((s, l)));
        var code = await p.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(7, code);
        await TestEnv.Eventually(() => lines.Any(l => l.Item2.StartsWith("crashing")));
        Assert.Contains((LogStream.Out, "line 1"), lines);
        Assert.Contains((LogStream.Err, "line 2 (stderr)"), lines);
    }

    [Fact]
    public async Task Kill_takes_down_the_whole_tree()
    {
        var pidFile = Path.GetTempFileName();
        using var p = ManagedProcess.Start(Spec($"--child --pid-file {pidFile}"), (_, _) => { });
        var pids = await TestEnv.WaitPids(pidFile);
        Assert.All(pids, pid => Assert.True(TestEnv.ProcessAlive(pid)));
        Assert.True(p.TreePids().Count >= 3); // cmd + fake + child

        await p.KillAsync();

        Assert.True(await TestEnv.Eventually(() => pids.All(pid => !TestEnv.ProcessAlive(pid))));
        File.Delete(pidFile);
    }

    [Fact]
    public async Task Dispose_kills_the_tree_like_a_daemon_crash_would()
    {
        var pidFile = Path.GetTempFileName();
        var p = ManagedProcess.Start(Spec($"--child --pid-file {pidFile}"), (_, _) => { });
        var pids = await TestEnv.WaitPids(pidFile);

        p.Dispose(); // closes the job handle, as the OS does when the daemon dies

        Assert.True(await TestEnv.Eventually(() => pids.All(pid => !TestEnv.ProcessAlive(pid))));
        File.Delete(pidFile);
    }

    [Fact]
    public async Task Environment_is_passed()
    {
        var lines = new ConcurrentQueue<string>();
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!,
            ["RM_TEST_VALUE"] = "hello world & more",
        };
        using var p = ManagedProcess.Start(Spec("--print-env RM_TEST_VALUE --crash-after 0.1 --exit-code 0", env), (_, l) => lines.Enqueue(l));
        await p.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await TestEnv.Eventually(() => lines.Contains("RM_TEST_VALUE=hello world & more")));
    }

    [Fact]
    public void Quoting_protects_cmd_metacharacters()
    {
        Assert.Equal("npm run dev -- --port 5173", ManagedProcess.BuildCommandLine("npm", ["run", "dev", "--", "--port", "5173"]));
        Assert.Equal("echo \"a&b\" \"x y\" \"\"", ManagedProcess.BuildCommandLine("echo", ["a&b", "x y", ""]));
    }

    [Fact]
    public void Port_inspector_sees_a_listener_and_its_pid()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            var owners = PortInspector.OwnersOf(port);
            Assert.Contains(owners, o => o.Pid == Environment.ProcessId);
            Assert.False(PortInspector.IsFree(port));
        }
        finally { l.Stop(); }
    }

    [Fact]
    public void Allocator_skips_taken_ports()
    {
        var a = new PortAllocator(p => p != 20001);
        var got = a.Allocate(new HashSet<int> { 20000 }, preferred: 20000);
        Assert.NotNull(got);
        Assert.NotEqual(20000, got);
        Assert.NotEqual(20001, got);
        Assert.InRange(got!.Value, PortAllocator.RangeStart, PortAllocator.RangeEnd);
    }
}
