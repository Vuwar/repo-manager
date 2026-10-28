using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RepoManager.Core.Agents;

/// <summary>
/// Sends a message into a Claude Code session's inbox (CLAUDE_CODE_MESSAGING_SOCKET, a named pipe on Windows).
/// Wire format, from the Claude Code binary (undocumented): an auth line, then a user line, each ending in \n.
/// </summary>
public static class AgentInbox
{
    private const string PipePrefix = @"\\.\pipe\";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Claude Code shows inbox messages as coming from "another Claude session", so a session would answer that
    /// session instead of the user. The first line says who typed it. It does not grant any permission.
    /// </summary>
    public static string FromTownUser(string text) =>
        "[From the user, typed in RepoManager Agent Town (their view of all sessions). Reply here in this session, as you would to them.]\n\n" + text;

    /// <summary>Never throws. The message never contains the token.</summary>
    public static async Task<(bool Ok, string Message)> SendAsync(string pipePath, string token, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, "message is empty");
        var name = pipePath.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase) ? pipePath[PipePrefix.Length..] : pipePath;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct);
            var payload =
                JsonSerializer.Serialize(new { type = "auth", token }, Json) + "\n" +
                JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = text } }, Json) + "\n";
            await pipe.WriteAsync(new UTF8Encoding(false).GetBytes(payload), ct);
            await pipe.FlushAsync(ct);
            return (true, "sent");
        }
        catch (Exception ex)
        {
            var reason = ex is TimeoutException ? "the session did not answer" : ex.Message.Replace(token, "***");
            return (false, "could not reach the session inbox: " + reason);
        }
    }
}
