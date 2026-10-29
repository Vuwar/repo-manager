using System.Diagnostics;
using RepoManager.Contracts;
using RepoManager.Core.Agents;

namespace RepoManager.Tests;

public class SessionStarterTests
{
    private const string Folder = @"C:\repos\demo";
    private readonly List<ProcessStartInfo> _started = [];
    private readonly List<string> _links = [];
    private readonly List<(string Socket, string Token, string Text)> _sent = [];
    private DateTimeOffset _now = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
    private Func<ProcessStartInfo, bool> _start;
    private (bool, string) _sendResult = (true, "sent");

    public SessionStarterTests() => _start = psi => { _started.Add(psi); return true; };

    private SessionStarter Starter() => new(
        psi => _start(psi),
        link => { _links.Add(link); return true; },
        () => _now,
        (socket, token, text) => { _sent.Add((socket, token, text)); return Task.FromResult(_sendResult); });

    private static AgentSessionDto Session(string id, string cwd, string surface = "terminal") => new()
    {
        Id = id, Folder = Path.GetFileName(cwd), Cwd = cwd, State = "idle", Surface = surface,
    };

    private static readonly (string, string)? Inbox = ("pipe", "tok");

    [Fact]
    public void Terminal_StartsWtWithFolderAndClaude_ArgumentList()
    {
        var status = Starter().StartTerminal(Folder, null);

        var psi = Assert.Single(_started);
        Assert.Equal("wt.exe", psi.FileName);
        Assert.Equal(["-d", Folder, "claude"], psi.ArgumentList);
        Assert.False(psi.UseShellExecute);
        Assert.Equal("none", status.State);
    }

    [Fact]
    public void Terminal_FallsBackToCmd_WhenWtFails()
    {
        _start = psi => { _started.Add(psi); return psi.FileName != "wt.exe"; };

        var status = Starter().StartTerminal(Folder, "hi");

        Assert.Equal(2, _started.Count);
        Assert.Equal("cmd.exe", _started[1].FileName);
        Assert.Equal(["/k", "claude"], _started[1].ArgumentList);
        Assert.Equal(Folder, _started[1].WorkingDirectory);
        Assert.False(_started[1].UseShellExecute);
        Assert.Equal("pending", status.State);
    }

    [Fact]
    public void Terminal_BothFail_IsFailed()
    {
        _start = _ => false;
        var s = Starter();
        var status = s.StartTerminal(Folder, "hi");
        Assert.Equal("failed", status.State);
        Assert.Equal("failed", s.Status(status.Id).State);
    }

    [Fact]
    public async Task HeldPrompt_SentOnMatchingTerminalSessionStart()
    {
        var s = Starter();
        var status = s.StartTerminal(Folder, "continue the login fix");

        await s.OnSessionStart(Session("a", Folder), Inbox);

        Assert.Equal(("pipe", "tok", AgentInbox.FromTownUser("continue the login fix")), Assert.Single(_sent));
        Assert.Equal("sent", s.Status(status.Id).State);
    }

    [Fact]
    public async Task HeldPrompt_IgnoresDesktopSessions_AndOtherFolders_MatchesNormalizedFolder()
    {
        var s = Starter();
        var status = s.StartTerminal(Folder, "hi");

        await s.OnSessionStart(Session("d", Folder, surface: "desktop"), Inbox);
        await s.OnSessionStart(Session("o", @"C:\repos\other"), Inbox);
        Assert.Empty(_sent);
        Assert.Equal("pending", s.Status(status.Id).State);

        await s.OnSessionStart(Session("a", @"c:\REPOS\demo\"), Inbox);
        Assert.Single(_sent);
    }

    [Fact]
    public async Task HeldPrompt_ExpiresAfter2Minutes()
    {
        var s = Starter();
        var status = s.StartTerminal(Folder, "hi");

        _now = _now.AddMinutes(2).AddSeconds(1);
        await s.OnSessionStart(Session("a", Folder), Inbox);

        Assert.Empty(_sent);
        Assert.Equal("expired", s.Status(status.Id).State);
    }

    [Fact]
    public async Task TwoStartsSameFolder_GoToTwoSessionsInOrder()
    {
        var s = Starter();
        var first = s.StartTerminal(Folder, "one");
        var second = s.StartTerminal(Folder, "two");

        await s.OnSessionStart(Session("a", Folder), ("pa", "ta"));
        await s.OnSessionStart(Session("b", Folder), ("pb", "tb"));
        await s.OnSessionStart(Session("c", Folder), ("pc", "tc"));

        Assert.Equal([("pa", "ta", AgentInbox.FromTownUser("one")), ("pb", "tb", AgentInbox.FromTownUser("two"))], _sent);
        Assert.Equal("sent", s.Status(first.Id).State);
        Assert.Equal("sent", s.Status(second.Id).State);
    }

    [Fact]
    public async Task SendFailure_IsFailedWithMessage()
    {
        _sendResult = (false, "pipe refused");
        var s = Starter();
        var status = s.StartTerminal(Folder, "hi");

        await s.OnSessionStart(Session("a", Folder), Inbox);

        var st = s.Status(status.Id);
        Assert.Equal("failed", st.State);
        Assert.Equal("pipe refused", st.Message);
    }

    [Fact]
    public async Task SessionWithoutInbox_IsFailed()
    {
        var s = Starter();
        var status = s.StartTerminal(Folder, "hi");

        await s.OnSessionStart(Session("a", Folder), null);

        Assert.Equal("failed", s.Status(status.Id).State);
        Assert.Empty(_sent);
    }

    [Fact]
    public void UnknownStatus_IsExpired()
    {
        Assert.Equal("expired", Starter().Status("nope").State);
    }

    [Fact]
    public void Desktop_OpensNewLink()
    {
        var r = Starter().StartDesktop(@"C:\a b");
        Assert.True(r.Ok);
        Assert.Equal(["claude://code/new?folder=C%3A%5Ca%20b"], _links);
        Assert.Equal("claude://code/new?folder=C%3A%5Ca%20b", SessionStarter.DesktopNewLink(@"C:\a b"));
    }
}
