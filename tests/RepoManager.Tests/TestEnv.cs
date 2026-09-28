using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RepoManager.Contracts;
using RepoManager.Core.Config;
using RepoManager.Core.Git;
using RepoManager.Core.Logs;
using RepoManager.Core.Processes;
using RepoManager.Core.State;

namespace RepoManager.Tests;

public static class TestEnv
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string FakeServerExe
    {
        get
        {
            var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
            var p = Path.Combine(RepoRoot, "tests", "FakeServer", "bin", config, "net10.0", "FakeServer.exe");
            if (!File.Exists(p)) throw new FileNotFoundException("FakeServer not built", p);
            return p;
        }
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "RepoManager.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public static bool ProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    /// <summary>Reads "pid childPid" written by FakeServer --pid-file; null while not written yet.</summary>
    public static int[]? ReadPids(string file)
    {
        try
        {
            var text = File.ReadAllText(file);
            return text.Contains(' ') ? text.Split(' ').Select(int.Parse).ToArray() : null;
        }
        catch (IOException) { return null; }
    }

    public static async Task<int[]> WaitPids(string file)
    {
        int[]? pids = null;
        Assert.True(await Eventually(() => (pids = ReadPids(file)) != null), "pid file not written");
        return pids!;
    }

    public static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}

/// <summary>Temp data dir + temp project folder + ConfigService/Supervisor wired together.</summary>
public sealed class Harness : IDisposable
{
    public string DataDir { get; }
    public string ProjectDir { get; }
    public string ProjectName => Path.GetFileName(ProjectDir);
    public ConfigService Config { get; }
    public StateStore State { get; }
    public LogStore Logs { get; }
    public Supervisor Supervisor { get; }

    public Harness(object devservers, SupervisorOptions? options = null, string? dataDir = null, string? projectDir = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-tests", Guid.NewGuid().ToString("N")[..8]);
        DataDir = dataDir ?? Path.Combine(root, "data");
        ProjectDir = projectDir ?? Path.Combine(root, "proj");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ProjectDir);
        WriteDevServers(devservers);
        File.WriteAllText(Path.Combine(DataDir, "registry.json"),
            JsonSerializer.Serialize(new RegistryFile { Projects = [new RegistryProject { Path = ProjectDir }] }, Protocol.Json));
        Config = new ConfigService(DataDir, new GitService(), watch: false);
        State = new StateStore(DataDir);
        Logs = new LogStore(Path.Combine(DataDir, "logs"));
        Supervisor = new Supervisor(Config, State, Logs, null, options ?? FastOptions);
    }

    public static SupervisorOptions FastOptions => new()
    {
        HealthInterval = TimeSpan.FromMilliseconds(200),
        HealthFailuresForUnhealthy = 2,
        UnhealthyNotifyAfter = TimeSpan.FromMilliseconds(500),
        AliveGrace = TimeSpan.FromMilliseconds(500),
        Backoff = _ => TimeSpan.FromMilliseconds(100),
    };

    public void WriteDevServers(object devservers) =>
        File.WriteAllText(Path.Combine(ProjectDir, "devservers.json"), JsonSerializer.Serialize(devservers, Protocol.Json));

    public string Id(string service) => $"{ProjectName}/{service}";

    public static object Fake(string args, object? extra = null)
    {
        var d = new Dictionary<string, object?> { ["command"] = TestEnv.FakeServerExe, ["args"] = SplitArgs(args) };
        if (extra != null)
            foreach (var p in extra.GetType().GetProperties()) d[p.Name] = p.GetValue(extra);
        return d;
    }

    private static List<string> SplitArgs(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    public void Dispose()
    {
        Supervisor.ShutdownAsync().Wait(15000);
        Supervisor.Dispose();
        Logs.Dispose();
        Config.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(DataDir)!, true); } catch { }
    }
}
