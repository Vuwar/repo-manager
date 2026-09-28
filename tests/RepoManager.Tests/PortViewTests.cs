using System.Net;
using System.Net.Sockets;
using RepoManager.Core.Ports;

namespace RepoManager.Tests;

public class PortViewTests
{
    private static readonly Dictionary<int, (string Name, ProcessDetails D)> Procs = new()
    {
        [4] = ("System", new ProcessDetails(null, false, true)),
        [1932] = ("svchost", new ProcessDetails(@"C:\WINDOWS\System32\svchost.exe", false, true)),
        [5084] = ("SCNotification", new ProcessDetails(@"C:\WINDOWS\CCM\SCNotification.exe", true, true)),
        [23152] = ("RepoManager", new ProcessDetails(@"C:\Users\me\AppData\Local\RepoManager\bin\RepoManager.exe", true, false)),
        [26768] = ("PanelPro.Api", new ProcessDetails(@"C:\repos\panel-pro\bin\PanelPro.Api.exe", true, false)),
        [36996] = ("node", new ProcessDetails(@"C:\Program Files\nodejs\node.exe", true, false)),
        [38296] = ("node", new ProcessDetails(@"C:\Program Files\nodejs\node.exe", true, false)),
        [31996] = ("Spotify", new ProcessDetails(@"C:\Users\me\AppData\Roaming\Spotify\Spotify.exe", true, false)),
        [5000] = ("devsvc", new ProcessDetails(@"C:\tools\devsvc.exe", true, false)),
    };

    private static readonly ListeningPort[] Listening =
    [
        new(135, 1932, "0.0.0.0"),
        new(445, 4, "0.0.0.0"),
        new(4000, 23152, "127.0.0.1"),
        new(5080, 26768, "127.0.0.1"),
        new(5173, 36996, "0.0.0.0"),
        new(5173, 36996, "[::]"),
        new(5173, 38296, "[::1]"),
        new(7768, 31996, "127.0.0.1"),
        new(49698, 5084, "127.0.0.1"),
        new(57621, 31996, "0.0.0.0"),
        new(51000, 5000, "127.0.0.1"),
    ];

    private static List<RepoManager.Contracts.PortDto> Build(params string[] hidden) =>
        PortView.Build(Listening,
            new Dictionary<int, string> { [26768] = "panel-pro/api", [36996] = "panel-pro/web", [5000] = "x/svc" },
            pid => Procs[pid].D, pid => Procs[pid].Name, new Dictionary<int, string>(), hidden, selfPid: 23152);

    [Fact]
    public void Dev_view_keeps_managed_and_own_processes_only()
    {
        var dev = Build().Where(p => p.Dev).Select(p => (p.Port, p.Pid)).ToList();
        Assert.Equal([(5080, 26768), (5173, 36996), (5173, 38296), (7768, 31996), (51000, 5000)], dev);
    }

    [Fact]
    public void Hidden_names_leave_the_dev_view()
    {
        Assert.DoesNotContain(Build("spotify"), p => p.Dev && p.ProcessName == "Spotify");
        Assert.True(Build("spotify").Single(p => p.Port == 7768).Hidden);
    }

    [Fact]
    public void Two_processes_on_one_port_is_a_conflict()
    {
        var rows = Build();
        Assert.All(rows.Where(p => p.Port == 5173), p => Assert.True(p.Conflict));
        Assert.Equal("0.0.0.0, [::]", rows.Single(p => p.Pid == 36996).Address);
        Assert.False(rows.Single(p => p.Port == 5080).Conflict);
    }

    [Fact]
    public void Kill_is_offered_only_for_own_unmanaged_processes()
    {
        var rows = Build();
        Assert.False(rows.Single(p => p.Port == 135).CanKill);
        Assert.False(rows.Single(p => p.Port == 49698).CanKill); // own process, but under C:\Windows
        Assert.False(rows.Single(p => p.Port == 4000).CanKill);  // RepoManager itself
        Assert.False(rows.Single(p => p.Port == 5080).CanKill);  // managed: stop the service instead
        Assert.True(rows.Single(p => p.Pid == 38296).CanKill);
        Assert.True(rows.Single(p => p.Port == 7768).CanKill);
    }

    [Fact]
    public void Process_info_recognises_this_process_and_system()
    {
        var me = ProcessInfo.Get(Environment.ProcessId);
        Assert.True(me.Mine);
        Assert.NotNull(me.Path);
        Assert.True(ProcessInfo.Get(4).System);
    }
}
