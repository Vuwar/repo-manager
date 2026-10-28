using System.Text;
using System.Text.Json;

namespace RepoManager.Core.Agents;

/// <summary>
/// Reads the end of a Claude Code session transcript (the hook's transcript_path, one JSON object per line) for
/// the session's title and every assistant message of its last turn. The Stop hook only carries the last one.
/// The format is Claude Code's internal one: anything unexpected is skipped, nothing throws.
/// </summary>
public static class TranscriptReader
{
    public const int DefaultMaxBytes = 1024 * 1024;

    /// <summary>Only <c>&lt;sessionId&gt;.jsonl</c> is read, so a hook cannot point the daemon at another file.</summary>
    public static (string? Title, string? LastTurn) Read(string? path, string sessionId, int maxBytes = DefaultMaxBytes)
    {
        if (string.IsNullOrEmpty(path)
            || !Path.GetFileName(path).Equals(sessionId + ".jsonl", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
            return (null, null);
        string tail;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, fs.Length - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var bytes = new byte[fs.Length - start];
            fs.ReadExactly(bytes);
            tail = Encoding.UTF8.GetString(bytes);
            // A read that starts mid-file starts mid-line: drop that partial line.
            if (start > 0) tail = tail[(tail.IndexOf('\n') + 1)..];
        }
        catch (IOException)
        {
            return (null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null);
        }

        string? customTitle = null, agentName = null;
        var turn = new List<string>();
        foreach (var line in tail.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object) continue;
                switch (Str(e, "type"))
                {
                    case "custom-title":
                        customTitle = Str(e, "customTitle") ?? customTitle;
                        break;
                    case "agent-name":
                        agentName = Str(e, "agentName") ?? agentName;
                        break;
                    case "queue-operation" when Str(e, "operation") == "dequeue":
                        turn.Clear();
                        break;
                    case "user" when IsPrompt(e):
                        turn.Clear();
                        break;
                    case "assistant" when !(e.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True):
                        if (e.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                            foreach (var block in content.EnumerateArray())
                                if (Str(block, "type") == "text" && Str(block, "text") is { Length: > 0 } text)
                                    turn.Add(text.Trim());
                        break;
                }
            }
            catch (JsonException)
            {
                // A half-written or foreign line: skip it.
            }
        }
        return (customTitle ?? agentName, turn.Count > 0 ? string.Join("\n\n", turn) : null);
    }

    /// <summary>A user entry that is a typed prompt, not a tool result.</summary>
    private static bool IsPrompt(JsonElement e)
    {
        if (!e.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var c)) return false;
        if (c.ValueKind == JsonValueKind.String) return true;
        return c.ValueKind == JsonValueKind.Array && c.EnumerateArray().Any(b => Str(b, "type") == "text");
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
