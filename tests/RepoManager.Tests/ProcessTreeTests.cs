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

    [Theory]
    [InlineData("local_02aa5842-e589-447f-977c-bcc353a67bbb", true)]
    [InlineData("local_x", true)]
    [InlineData("02aa5842", false)]
    [InlineData("local_a&b=c", false)]
    [InlineData(null, false)]
    public void DesktopLink_OnlyForValidHostIds(string? host, bool ok) =>
        Assert.Equal(ok ? "claude://code/continue?session=" + host : null, WindowActivator.DesktopLink(host));
}
