using RepoManager.Contracts;
using RepoManager.Core.Agents;
using RepoManager.Core.Config;

namespace RepoManager.Tests;

public class AttentionTrackerTests
{
    private DateTimeOffset _now = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
    private readonly AttentionTracker _t;
    private static readonly TownSettings On = new();

    public AttentionTrackerTests() => _t = new AttentionTracker(() => _now);

    private static AgentSessionDto S(string id, string state, string? project = "kodex", string message = "", IReadOnlyList<PermissionPromptDto>? prompts = null) => new()
    {
        Id = id,
        Project = project,
        Folder = "folder-" + id,
        Cwd = "C:\\r\\" + id,
        State = state,
        Surface = "desktop",
        Message = message,
        Prompts = prompts ?? [],
    };

    private static PermissionPromptDto Prompt(string tool, string summary) => new() { RequestId = "r", ToolName = tool, Summary = summary, Answerable = false };

    private void Later(double seconds = 3) => _now += TimeSpan.FromSeconds(seconds);

    [Fact]
    public void EnteringWaiting_NotifiesOnce()
    {
        var u = _t.OnSession(S("a", "waiting", prompts: [Prompt("Bash", "npm install")]), false, On);
        Assert.Equal(new AttentionNotice("a", "kodex: wants to run npm install", ""), u.Notice);
        Assert.Equal(AttentionSound.NeedsYou, u.Sound);
        Assert.True(u.Blink);
        Later();
        var again = _t.OnSession(S("a", "waiting", prompts: [Prompt("Bash", "npm install")]), false, On);
        Assert.Null(again.Notice);
        Assert.Null(again.Sound);
        Assert.True(again.Blink);
    }

    [Fact]
    public void WaitingAgainAfterWork_NotifiesAgain()
    {
        _t.OnSession(S("a", "waiting"), false, On);
        Later();
        _t.OnSession(S("a", "working"), false, On);
        Later(AttentionTracker.NoticeGap.TotalSeconds);
        Assert.NotNull(_t.OnSession(S("a", "waiting"), false, On).Notice);
    }

    [Theory]
    [InlineData("Write", "Write", "kodex: wants to use Write")]
    [InlineData("PowerShell", "Get-Item x", "kodex: wants to run Get-Item x")]
    public void NoticeTitle_PerTool(string tool, string summary, string title) =>
        Assert.Equal(title, _t.OnSession(S("a", "waiting", prompts: [Prompt(tool, summary)]), false, On).Notice?.Title);

    [Fact]
    public void Question_UsesFolderWithoutProject_AndClipsTheBody()
    {
        var u = _t.OnSession(S("a", "waiting", project: null, message: new string('x', 300)), false, On);
        Assert.Equal("folder-a: has a question", u.Notice?.Title);
        Assert.Equal(200, u.Notice?.Body.Length);
    }

    [Fact]
    public void Focused_NoNoticeNoBlink_SoundStillPlays()
    {
        var u = _t.OnSession(S("a", "waiting"), true, On);
        Assert.Null(u.Notice);
        Assert.False(u.Blink);
        Assert.Equal(AttentionSound.NeedsYou, u.Sound);
    }

    [Fact]
    public void FocusLost_WhileWaiting_BlinksWithoutASecondNotice()
    {
        _t.OnSession(S("a", "waiting"), true, On);
        Later();
        var u = _t.OnFocus(false, On);
        Assert.True(u.Blink);
        Assert.Null(u.Notice);
        Assert.False(_t.OnFocus(true, On).Blink);
    }

    [Fact]
    public void SessionEnded_StopsBlinking()
    {
        _t.OnSession(S("a", "waiting"), false, On);
        Assert.False(_t.OnEnded("a", false, On).Blink);
    }

    [Fact]
    public void Done_SoftSoundAndBadge_ClearedByFocus()
    {
        _t.OnSession(S("a", "working"), false, On);
        Later();
        var u = _t.OnSession(S("a", "done"), false, On);
        Assert.Equal(AttentionSound.Done, u.Sound);
        Assert.True(u.DoneBadge);
        Assert.Null(u.Notice);
        Assert.False(_t.OnFocus(true, On).DoneBadge);
        Assert.False(_t.OnFocus(false, On).DoneBadge);
    }

    [Fact]
    public void Done_WhileFocused_NoBadge()
    {
        _t.OnSession(S("a", "working"), true, On);
        Later();
        Assert.False(_t.OnSession(S("a", "done"), true, On).DoneBadge);
    }

    [Fact]
    public void Sounds_AtMostOneEveryTwoSeconds()
    {
        Assert.NotNull(_t.OnSession(S("a", "waiting"), false, On).Sound);
        Later(1);
        var second = _t.OnSession(S("b", "waiting"), false, On);
        Assert.Null(second.Sound);
        Assert.Null(second.Notice); // within the notice gap too
        Later(1.5);
        Assert.NotNull(_t.OnSession(S("c", "waiting"), false, On).Sound);
    }

    [Fact]
    public void SettingsOff_SilenceEachPart()
    {
        var off = new TownSettings { Tray = false, Notifications = false, Sound = false };
        var u = _t.OnSession(S("a", "waiting"), false, off);
        Assert.Null(u.Notice);
        Assert.Null(u.Sound);
        Assert.False(u.Blink);
    }

    [Fact]
    public void Notices_AtMostOnePerTenSeconds_NoBalloonStorm()
    {
        Assert.NotNull(_t.OnSession(S("a", "waiting"), false, On).Notice);
        Later(1);
        Assert.Null(_t.OnSession(S("b", "waiting"), false, On).Notice);
        Later(1);
        Assert.Null(_t.OnSession(S("c", "waiting"), false, On).Notice);
        Later(9);
        Assert.NotNull(_t.OnSession(S("d", "waiting"), false, On).Notice);
    }

    [Fact]
    public void DoneBadge_FollowsTheTraySetting()
    {
        _t.OnSession(S("a", "working"), false, On);
        Later();
        Assert.False(_t.OnSession(S("a", "done"), false, new TownSettings { Tray = false }).DoneBadge);
    }

    [Fact]
    public void DoneBadge_GoesWhenThatSessionWorksAgainOrEnds()
    {
        _t.OnSession(S("a", "working"), false, On);
        Later();
        Assert.True(_t.OnSession(S("a", "done"), false, On).DoneBadge);
        Later();
        Assert.False(_t.OnSession(S("a", "working"), false, On).DoneBadge);
        Later();
        Assert.True(_t.OnSession(S("a", "done"), false, On).DoneBadge);
        Assert.False(_t.OnEnded("a", false, On).DoneBadge);
    }

    [Fact]
    public void FirstSeenAlreadyDone_NoSound()
    {
        // After a daemon restart the first event of a finished session is not a new finish.
        Assert.Null(_t.OnSession(S("a", "idle"), false, On).Sound);
    }
}
