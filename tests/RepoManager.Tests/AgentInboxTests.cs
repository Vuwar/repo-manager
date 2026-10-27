using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using RepoManager.Core.Agents;

namespace RepoManager.Tests;

public class AgentInboxTests
{
    /// <summary>A one-shot inbox: accepts one connection and returns everything written to it.</summary>
    internal static (string Path, Task<string> Received) FakeInbox()
    {
        var name = "rm-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        async Task<string> Receive()
        {
            await using (server)
            {
                await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(server, new UTF8Encoding(false));
                return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        return (@"\\.\pipe\" + name, Receive());
    }

    [Fact]
    public async Task Send_WritesAuthThenUserLine()
    {
        var (path, received) = FakeInbox();
        var result = await AgentInbox.SendAsync(path, "tok", "hello \"x\"\nline2", CancellationToken.None);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(
            "{\"type\":\"auth\",\"token\":\"tok\"}\n" +
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hello \\\"x\\\"\\nline2\"}}\n",
            await received);
    }

    [Fact]
    public async Task Send_NonAscii_IsReadableUtf8()
    {
        var (path, received) = FakeInbox();
        await AgentInbox.SendAsync(path, "tok", "çağ <b>", CancellationToken.None);
        Assert.Contains("\"content\":\"çağ <b>\"", await received);
    }

    [Fact]
    public async Task Send_MissingPipe_FailsFast()
    {
        var sw = Stopwatch.StartNew();
        var result = await AgentInbox.SendAsync(@"\\.\pipe\rm-test-missing-" + Guid.NewGuid().ToString("N"), "secret-tok", "hi", CancellationToken.None);
        Assert.False(result.Ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.5), sw.Elapsed.ToString());
        Assert.Contains("inbox", result.Message);
        Assert.DoesNotContain("secret-tok", result.Message);
    }

    [Fact]
    public async Task Send_EmptyText_Rejected()
    {
        var result = await AgentInbox.SendAsync(@"\\.\pipe\unused", "tok", "  \n ", CancellationToken.None);
        Assert.False(result.Ok);
    }
}
