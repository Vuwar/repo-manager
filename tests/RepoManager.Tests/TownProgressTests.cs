using RepoManager.Contracts;
using RepoManager.Core.Agents;

namespace RepoManager.Tests;

public class TownProgressTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-tests", "xp-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTimeOffset _now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    public TownProgressTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private TownProgress New() => new(_dir, () => _now);

    private static AgentSessionDto S(string id, string state, string? project = "kodex") =>
        new() { Id = id, Project = project, Folder = "f", Cwd = "C:\\r", State = state, Surface = "desktop" };

    [Theory]
    [InlineData(0, 1, 0, 50)]
    [InlineData(49, 1, 0, 50)]
    [InlineData(50, 2, 50, 150)]
    [InlineData(149, 2, 50, 150)]
    [InlineData(150, 3, 150, 300)]
    [InlineData(300, 4, 300, 500)]
    public void LevelOf_Curve(int xp, int level, int start, int next) =>
        Assert.Equal(new LevelDto(xp, level, start, next), TownProgress.LevelOf(xp));

    [Fact]
    public void Answered_FastBonusWithin30Seconds()
    {
        var p = New();
        Assert.Equal(20, p.Answered("s1", TimeSpan.FromSeconds(30))!.Amount);
        Assert.Equal(10, p.Answered("s1", TimeSpan.FromSeconds(31))!.Amount);
        Assert.Equal(30, p.Snapshot().Player.Xp);
    }

    [Fact]
    public void Sent_ThenWorkingWithinTwoMinutes_FiveOnce()
    {
        var p = New();
        p.SessionChanged(S("s1", "done"));
        p.Sent("s1");
        _now += TimeSpan.FromMinutes(1);
        var award = p.SessionChanged(S("s1", "working"));
        Assert.Equal(new XpAwardDto("send", 5, "s1", null), award);
        p.SessionChanged(S("s1", "done"));
        Assert.Null(p.SessionChanged(S("s1", "working")));
    }

    [Fact]
    public void Sent_TooLongAgo_Nothing()
    {
        var p = New();
        p.SessionChanged(S("s1", "done"));
        p.Sent("s1");
        _now += TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1);
        Assert.Null(p.SessionChanged(S("s1", "working")));
    }

    [Fact]
    public void Turn_GivesTheRepoTen_TemporaryLotsNothing()
    {
        var p = New();
        p.SessionChanged(S("s1", "working"));
        Assert.Equal(new XpAwardDto("turn", 10, "s1", "kodex"), p.SessionChanged(S("s1", "done")));
        Assert.Null(p.SessionChanged(S("s1", "done")));
        p.SessionChanged(S("t1", "working", project: null));
        Assert.Null(p.SessionChanged(S("t1", "done", project: null)));
        Assert.Equal(10, p.Snapshot().Repos["kodex"].Xp);
    }

    [Fact]
    public void Awarded_RaisedWithTheNewTotals()
    {
        var p = New();
        TownProgressDto? seen = null;
        p.Awarded += (progress, _) => seen = progress;
        p.Answered("s1", TimeSpan.Zero);
        Assert.Equal(20, seen?.Player.Xp);
    }

    [Fact]
    public void Persists_AcrossRestarts_Atomically()
    {
        var p = New();
        p.Answered("s1", TimeSpan.Zero);
        p.SessionChanged(S("s1", "working"));
        p.SessionChanged(S("s1", "done"));
        var again = New().Snapshot();
        Assert.Equal(20, again.Player.Xp);
        Assert.Equal(10, again.Repos["kodex"].Xp);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void CorruptFile_StartsFromZero()
    {
        File.WriteAllText(Path.Combine(_dir, TownProgress.FileName), "{not json");
        Assert.Equal(0, New().Snapshot().Player.Xp);
        File.WriteAllText(Path.Combine(_dir, TownProgress.FileName), "[1,2]");
        Assert.Equal(0, New().Snapshot().Player.Xp);
    }

    [Fact]
    public void HugeXpInAnEditedFile_IsClamped_AndNeverHangs()
    {
        File.WriteAllText(Path.Combine(_dir, TownProgress.FileName), "{\"player\":2147483647,\"repos\":{\"kodex\":-5}}");
        var task = Task.Run(() => New().Snapshot());
        Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "loading hung");
        Assert.Equal(TownProgress.MaxXp, task.Result.Player.Xp);
        Assert.Equal(0, task.Result.Repos["kodex"].Xp);
        Assert.True(Task.Run(() => TownProgress.LevelOf(int.MaxValue)).Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Sent_WhileAlreadyWorking_DoesNotPayForALaterPrompt()
    {
        var p = New();
        p.SessionChanged(S("s1", "working"));
        p.Sent("s1");
        p.SessionChanged(S("s1", "done"));
        _now += TimeSpan.FromSeconds(30);
        // The next prompt is typed in the terminal, not sent from the town.
        Assert.Null(p.SessionChanged(S("s1", "working")));
    }

    [Fact]
    public void RemovedRepo_KeepsItsXp_NewRepoStartsAtZero()
    {
        var p = New();
        p.SessionChanged(S("s1", "working", project: "old-name"));
        p.SessionChanged(S("s1", "done", project: "old-name"));
        var again = New();
        Assert.Equal(10, again.Snapshot().Repos["old-name"].Xp);
        Assert.False(again.Snapshot().Repos.ContainsKey("new-name"));
    }
}
