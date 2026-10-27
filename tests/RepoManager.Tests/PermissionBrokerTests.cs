using RepoManager.Contracts;
using RepoManager.Core.Agents;

namespace RepoManager.Tests;

public class PermissionBrokerTests
{
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(5);
    private readonly PermissionBroker _broker = new();

    private static PermissionPromptDto Prompt(string id) =>
        new() { RequestId = id, ToolName = "Bash", Summary = "npm install", Answerable = true };

    [Fact]
    public async Task Unfocused_ReturnsNoDecisionImmediately()
    {
        var task = _broker.WaitAsync("s1", Prompt("r1"), Long, CancellationToken.None);
        Assert.True(task.IsCompleted);
        Assert.Null((await task).Behavior);
        var listed = Assert.Single(_broker.PromptsFor("s1"));
        Assert.False(listed.Answerable);
        _broker.ClearSession("s1");
        Assert.Empty(_broker.PromptsFor("s1"));
    }

    [Fact]
    public async Task Focused_WaitsForAnswer()
    {
        _broker.SetFocused(true);
        var task = _broker.WaitAsync("s1", Prompt("r1"), Long, CancellationToken.None);
        Assert.False(task.IsCompleted);
        Assert.True(Assert.Single(_broker.PromptsFor("s1")).Answerable);
        Assert.True(_broker.Answer("r1", new("allow", false)));
        Assert.Equal("allow", (await task.WaitAsync(TimeSpan.FromSeconds(5))).Behavior);
        Assert.Empty(_broker.PromptsFor("s1"));
    }

    [Fact]
    public async Task FocusLost_ReleasesAll()
    {
        _broker.SetFocused(true);
        var a = _broker.WaitAsync("s1", Prompt("r1"), Long, CancellationToken.None);
        var b = _broker.WaitAsync("s2", Prompt("r2"), Long, CancellationToken.None);
        _broker.SetFocused(false);
        Assert.Null((await a.WaitAsync(TimeSpan.FromSeconds(5))).Behavior);
        Assert.Null((await b.WaitAsync(TimeSpan.FromSeconds(5))).Behavior);
        Assert.Empty(_broker.PromptsFor("s1"));
    }

    [Fact]
    public async Task TwoPromptsSameSession_Independent()
    {
        _broker.SetFocused(true);
        var a = _broker.WaitAsync("s1", Prompt("r1"), Long, CancellationToken.None);
        var b = _broker.WaitAsync("s1", Prompt("r2"), Long, CancellationToken.None);
        _broker.Answer("r1", new("deny", false));
        Assert.Equal("deny", (await a.WaitAsync(TimeSpan.FromSeconds(5))).Behavior);
        await Task.Delay(50);
        Assert.False(b.IsCompleted);
        Assert.Equal("r2", Assert.Single(_broker.PromptsFor("s1")).RequestId);
    }

    [Fact]
    public async Task Timeout_ReturnsNoDecision()
    {
        _broker.SetFocused(true);
        var result = await _broker.WaitAsync("s1", Prompt("r1"), TimeSpan.FromMilliseconds(100), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(result.Behavior);
        Assert.Empty(_broker.PromptsFor("s1"));
    }

    [Fact]
    public async Task Cancelled_ReturnsNoDecision()
    {
        _broker.SetFocused(true);
        using var cts = new CancellationTokenSource();
        var task = _broker.WaitAsync("s1", Prompt("r1"), Long, cts.Token);
        cts.Cancel();
        Assert.Null((await task.WaitAsync(TimeSpan.FromSeconds(5))).Behavior);
        Assert.Empty(_broker.PromptsFor("s1"));
    }

    [Fact]
    public void Answer_UnknownId_ReturnsFalse() => Assert.False(_broker.Answer("nope", new("allow", false)));

    [Fact]
    public void PromptsChanged_RaisedOnAddAndRemove()
    {
        var raised = new List<string>();
        _broker.PromptsChanged += raised.Add;
        _broker.SetFocused(true);
        _broker.WaitAsync("s1", Prompt("r1"), Long, CancellationToken.None);
        _broker.Answer("r1", new("allow", false));
        Assert.Equal(["s1", "s1"], raised);
    }
}
