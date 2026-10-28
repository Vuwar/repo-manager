using System.Diagnostics;
using RepoManager.Core.Agents;

namespace RepoManager.Tests;

public class ProcessTreeTests
{
    private static Process Child() => Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 5 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;

    [Fact]
    public void Ancestors_OfChild_StartsWithUs()
    {
        using var child = Child();
        try { Assert.Equal(Environment.ProcessId, ProcessTree.Ancestors(child.Id)[0].Pid); }
        finally { child.Kill(true); }
    }

    [Fact]
    public void FindAncestor_ByName()
    {
        using var child = Child();
        try
        {
            var me = Process.GetCurrentProcess().ProcessName + ".exe";
            Assert.Equal(Environment.ProcessId, ProcessTree.FindAncestor(child.Id, n => n.Equals(me, StringComparison.OrdinalIgnoreCase)));
        }
        finally { child.Kill(true); }
    }

    [Fact]
    public void Ancestors_UnknownPid_Empty() => Assert.Empty(ProcessTree.Ancestors(int.MaxValue - 7));

    [Fact]
    public void WindowOwner_FirstWithAWindow()
    {
        var chain = new[] { (10, "claude.exe", false), (11, "pwsh.exe", false), (12, "WindowsTerminal.exe", true), (13, "explorer.exe", true) };
        Assert.Equal(12, WindowActivator.WindowOwner(chain));
    }

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("RepoManager.exe")]
    [InlineData("services.exe")]
    [InlineData("svchost.exe")]
    public void WindowOwner_StopsAtSystemAndShellProcesses(string stop)
    {
        // A classic console session: conhost is a sibling, so the chain reaches the desktop shell first.
        var chain = new[] { (10, "claude.exe", false), (11, "cmd.exe", false), (12, stop, true), (13, "Code.exe", true) };
        Assert.Null(WindowActivator.WindowOwner(chain));
    }

    [Theory]
    [InlineData("local_02aa5842-e589-447f-977c-bcc353a67bbb", true)]
    [InlineData("local_x", true)]
    [InlineData("02aa5842", false)]
    [InlineData("local_a&b=c", false)]
    [InlineData(null, false)]
    public void DesktopLink_OnlyForValidHostIds(string? host, bool ok) =>
        Assert.Equal(ok ? "claude://code/continue?session=" + host : null, WindowActivator.DesktopLink(host));
}
