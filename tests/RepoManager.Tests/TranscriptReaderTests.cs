using System.Text.Json;
using RepoManager.Core.Agents;

namespace RepoManager.Tests;

public class TranscriptReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-tests", "tr-" + Guid.NewGuid().ToString("N")[..8]);
    private const string Id = "d1ff0e24-829f-4fb5-9e31-2f8e181d817a";

    public TranscriptReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string User(string text) => JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = text } });
    private static string ToolResult() => JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = new[] { new { type = "tool_result", content = "ok" } } } });
    private static string Assistant(string id, string text, bool sidechain = false) =>
        JsonSerializer.Serialize(new { type = "assistant", isSidechain = sidechain, message = new { id, role = "assistant", content = new[] { new { type = "text", text } } } });
    private static string ToolUse(string id) =>
        JsonSerializer.Serialize(new { type = "assistant", message = new { id, role = "assistant", content = new[] { new { type = "tool_use", name = "Bash" } } } });

    private string Write(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void LastTurn_AllAssistantTextSinceTheLastPrompt()
    {
        var path = Write(Id + ".jsonl",
            User("first"), Assistant("m0", "old answer"),
            User("second"), Assistant("m1", "I told another session."), ToolUse("m2"), ToolResult(),
            Assistant("m3", "Sidechain noise", sidechain: true), Assistant("m4", "It could not retry."),
            JsonSerializer.Serialize(new { type = "api-request-blob", message = new { content = new[] { new { type = "text", text = "dup" } } } }));
        Assert.Equal("I told another session.\n\nIt could not retry.", TranscriptReader.Read(path, Id).LastTurn);
    }

    [Fact]
    public void LastTurn_StartsAgainAfterAQueuedMessage()
    {
        var path = Write(Id + ".jsonl",
            User("go"), Assistant("m1", "before"),
            JsonSerializer.Serialize(new { type = "queue-operation", operation = "enqueue" }),
            JsonSerializer.Serialize(new { type = "queue-operation", operation = "dequeue" }),
            Assistant("m2", "after"));
        Assert.Equal("after", TranscriptReader.Read(path, Id).LastTurn);
    }

    [Fact]
    public void Title_CustomTitleWinsOverAgentName()
    {
        var path = Write(Id + ".jsonl",
            JsonSerializer.Serialize(new { type = "agent-name", agentName = "Old name" }),
            JsonSerializer.Serialize(new { type = "custom-title", customTitle = "Error investigation" }),
            JsonSerializer.Serialize(new { type = "agent-name", agentName = "Error investigation 2" }));
        Assert.Equal("Error investigation", TranscriptReader.Read(path, Id).Title);
        Assert.Equal("Only name", TranscriptReader.Read(Write(Id + ".jsonl", JsonSerializer.Serialize(new { type = "agent-name", agentName = "Only name" })), Id).Title);
    }

    [Fact]
    public void ReadsOnlyTheTail_SkippingAHalfLine()
    {
        var big = Enumerable.Range(0, 3000).Select(i => Assistant("x" + i, new string('a', 500))).ToList();
        big.Add(User("latest"));
        big.Add(Assistant("last", "the end"));
        var path = Write(Id + ".jsonl", [.. big]);
        Assert.Equal("the end", TranscriptReader.Read(path, Id, maxBytes: 64 * 1024).LastTurn);
    }

    [Theory]
    [InlineData("other-id.jsonl")]
    [InlineData(Id + ".txt")]
    public void Rejects_FilesThatAreNotThisSessionsTranscript(string name)
    {
        var path = Write(name, User("x"), Assistant("m", "secret"));
        Assert.Equal((null, null), TranscriptReader.Read(path, Id));
    }

    [Fact]
    public void MissingFileOrBadLines_NeverThrow()
    {
        Assert.Equal((null, null), TranscriptReader.Read(Path.Combine(_dir, Id + ".jsonl"), Id));
        var path = Write(Id + ".jsonl", "{not json", User("x"), "", Assistant("m", "fine"));
        Assert.Equal("fine", TranscriptReader.Read(path, Id).LastTurn);
    }
}
