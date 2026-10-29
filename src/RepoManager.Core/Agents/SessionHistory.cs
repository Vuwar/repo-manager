using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoManager.Contracts;

namespace RepoManager.Core.Agents;

/// <summary>
/// Past Claude Code sessions, read from their transcripts (<c>&lt;projects dir&gt;\&lt;folder&gt;\&lt;id&gt;.jsonl</c>) and the
/// desktop app's session index (<c>local_*.json</c>). Only files are read: nothing starts Claude or spends tokens.
/// Both formats are internal to Claude: anything unexpected is skipped, nothing throws.
/// </summary>
public sealed partial class SessionHistory(string projectsDir, string desktopDir, Func<string, (string Project, string? Worktree)?> resolve)
{
    public const int PageSize = 150;
    public const int HandoffMax = 4000;
    private const int EdgeBytes = 64 * 1024;
    /// <summary>How far the list reads from the start looking for the first prompt.</summary>
    private const int HeadMax = 8 * 1024 * 1024;
    private const int HandoffPrompts = 15;
    private const int PromptMax = 200;
    private const int ReplyMax = 1500;

    [GeneratedRegex("^[0-9a-fA-F-]{36}$")]
    private static partial Regex IdPattern();

    public static bool ValidId(string? id) => id != null && IdPattern().IsMatch(id);

    /// <summary>Transcript bytes read so far (the list reads only each file's ends and caches them).</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    private long _bytesRead;

    private sealed record Summary(string Id, string Path, string Cwd, string? Entrypoint, string? Title, string FirstPrompt, DateTimeOffset Started, DateTimeOffset LastActivity);
    private sealed record IndexEntry(string HostId, string? Title, bool Archived, DateTimeOffset? LastActivity);

    private readonly ConcurrentDictionary<string, (long Size, DateTime Mtime, Summary? Summary)> _summaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _pageLock = new();
    private (string Path, long Size, DateTime Mtime, List<TranscriptEntryDto> Entries)? _lastEntries;

    /// <summary>The project's past sessions, newest first, without the ones in <paramref name="liveIds"/>.</summary>
    public IReadOnlyList<PastSessionDto> List(string project, IReadOnlySet<string> liveIds)
    {
        var index = ReadIndex();
        var result = new List<PastSessionDto>();
        foreach (var path in Transcripts())
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (liveIds.Contains(id) || SummaryOf(path) is not { } s) continue;
            if (ToDto(s, index) is { } dto && string.Equals(dto.Project, project, StringComparison.OrdinalIgnoreCase)) result.Add(dto);
        }
        return result.OrderByDescending(s => s.LastActivity).ToList();
    }

    public PastSessionDto? Find(string id) =>
        TranscriptPath(id) is { } path && SummaryOf(path) is { } s ? ToDto(s, ReadIndex()) : null;

    /// <summary>The transcript file of a session id, or null for an invalid or unknown id.</summary>
    public string? TranscriptPath(string id)
    {
        if (!ValidId(id) || !Directory.Exists(projectsDir)) return null;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(projectsDir))
            {
                var path = Path.Combine(dir, id + ".jsonl");
                if (File.Exists(path)) return path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Entries [max(0, end - 150), end) where end is <paramref name="before"/> or the total.</summary>
    public TranscriptPageDto? Page(string id, int? before)
    {
        if (TranscriptPath(id) is not { } path || EntriesOf(path) is not { } all) return null;
        var end = Math.Clamp(before ?? all.Count, 0, all.Count);
        var start = Math.Max(0, end - PageSize);
        return new TranscriptPageDto { Id = id, Entries = all.GetRange(start, end - start), Start = start, Total = all.Count };
    }

    /// <summary>The first message of a new session that continues this one: built from the transcript, no Claude call.</summary>
    public string? Handoff(string id)
    {
        if (Find(id) is not { } s || TranscriptPath(id) is not { } path || EntriesOf(path) is not { } all) return null;
        var prompts = all.Where(e => e.Role == "user").Select(e => Shorten(OneLine(e.Text), PromptMax)).ToList();
        var reply = all.LastOrDefault(e => e.Role == "assistant")?.Text;
        var head = $"This continues an earlier session: \"{s.Title}\" ({s.Started.ToLocalTime():yyyy-MM-dd HH:mm}).";
        var tail = "Full transcript (read it only if you need details): " + path;

        for (var keep = Math.Min(HandoffPrompts, prompts.Count); ; keep--)
        {
            var sb = new StringBuilder().Append(head).Append('\n').Append("What I asked:\n");
            if (prompts.Count > keep) sb.Append($"- (… {prompts.Count - keep} earlier)\n");
            foreach (var p in prompts.Skip(prompts.Count - keep)) sb.Append("- ").Append(p).Append('\n');
            var room = HandoffMax - sb.Length - tail.Length - "Your last reply:\n".Length - 2;
            // Room for at least the start of the reply, else one prompt fewer.
            if (room >= (reply == null ? 0 : Math.Min(reply.Length, 200)) || keep == 0)
            {
                if (reply != null && room > 0) sb.Append("Your last reply:\n").Append(Shorten(reply.Trim(), Math.Min(ReplyMax, room))).Append('\n');
                return sb.Append(tail).ToString();
            }
        }
    }

    private static string OneLine(string s) => string.Join(' ', s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private IEnumerable<string> Transcripts()
    {
        if (!Directory.Exists(projectsDir)) return [];
        try
        {
            // Top level of each project folder only: subagent transcripts live in subfolders.
            return Directory.EnumerateDirectories(projectsDir).SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl")).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private PastSessionDto? ToDto(Summary s, Dictionary<string, IndexEntry> index)
    {
        index.TryGetValue(s.Id, out var ix);
        var place = resolve(s.Cwd);
        return new PastSessionDto
        {
            Id = s.Id,
            Project = place?.Project,
            Worktree = place?.Worktree,
            Title = ix?.Title is { Length: > 0 } t ? t : s.Title ?? AgentRegistry.Clip(OneLine(s.FirstPrompt), AgentRegistry.TitleMax),
            Surface = ix != null || s.Entrypoint?.StartsWith("claude-desktop", StringComparison.Ordinal) == true ? "desktop" : "terminal",
            HostSessionId = ix?.HostId,
            Archived = ix?.Archived ?? false,
            Started = s.Started,
            LastActivity = ix?.LastActivity ?? s.LastActivity,
            Cwd = s.Cwd,
        };
    }

    private Dictionary<string, IndexEntry> ReadIndex()
    {
        var map = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(desktopDir)) return map;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(desktopDir, "local_*.json", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return map;
        }
        foreach (var file in files)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var e = doc.RootElement;
                if (TranscriptReader.Str(e, "cliSessionId") is not { } cli || TranscriptReader.Str(e, "sessionId") is not { } host) continue;
                DateTimeOffset? last = e.TryGetProperty("lastActivityAt", out var la) && la.TryGetInt64(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
                var archived = e.TryGetProperty("isArchived", out var a) && a.ValueKind == JsonValueKind.True;
                map[cli] = new IndexEntry(host, TranscriptReader.Str(e, "title"), archived, last);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentOutOfRangeException) { }
        }
        return map;
    }

    private Summary? SummaryOf(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (_summaries.TryGetValue(path, out var c) && c.Size == info.Length && c.Mtime == info.LastWriteTimeUtc) return c.Summary;
        var s = Summarize(path, info);
        _summaries[path] = (info.Length, info.LastWriteTimeUtc, s);
        return s;
    }

    private Summary? Summarize(string path, FileInfo info)
    {
        string? cwd = null, entrypoint = null, firstPrompt = null, custom = null, agent = null, ai = null;
        DateTimeOffset? started = null, last = null;
        void See(JsonElement e)
        {
            switch (TranscriptReader.Str(e, "type"))
            {
                case "custom-title": custom = TranscriptReader.Str(e, "customTitle") ?? custom; break;
                case "agent-name": agent = TranscriptReader.Str(e, "agentName") ?? agent; break;
                case "ai-title": ai = TranscriptReader.Str(e, "aiTitle") ?? ai; break;
                case "user" when firstPrompt == null && IsUserPrompt(e):
                    firstPrompt = PromptText(e);
                    cwd = TranscriptReader.Str(e, "cwd");
                    entrypoint = TranscriptReader.Str(e, "entrypoint");
                    started = Time(e);
                    break;
            }
            if (Time(e) is { } t) last = t;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = fs.Length;
            using var reader = new StreamReader(fs, Encoding.UTF8, false, 16 * 1024, leaveOpen: true);
            // From the start up to the first typed prompt (a pasted image makes that line long), then the last
            // EdgeBytes for later titles and the last activity. A small file is read whole.
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                Parse(line, See);
                if (len > 2 * EdgeBytes && (firstPrompt != null || fs.Position > HeadMax)) break;
            }
            var read = fs.Position;
            if (line != null)
            {
                if (len - EdgeBytes > fs.Position)
                {
                    read += EdgeBytes;
                    fs.Seek(len - EdgeBytes, SeekOrigin.Begin);
                    reader.DiscardBufferedData();
                    reader.ReadLine(); // starts mid-line
                }
                else read = len;
                while ((line = reader.ReadLine()) != null) Parse(line, See);
            }
            Interlocked.Add(ref _bytesRead, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        if (firstPrompt == null || cwd == null) return null;
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        return new Summary(Path.GetFileNameWithoutExtension(path), path, cwd, entrypoint, custom ?? agent ?? ai, firstPrompt, started ?? mtime, last ?? mtime);
    }

    private List<TranscriptEntryDto>? EntriesOf(string path)
    {
        lock (_pageLock)
        {
            try
            {
                var info = new FileInfo(path);
                if (_lastEntries is { } c && c.Path == path && c.Size == info.Length && c.Mtime == info.LastWriteTimeUtc) return c.Entries;
                var entries = new List<TranscriptEntryDto>();
                // Line by line: a transcript can be hundreds of MB, and the daemon always runs.
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs, Encoding.UTF8, false, 64 * 1024))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null) Parse(line, e => AddEntries(e, entries));
                    Interlocked.Add(ref _bytesRead, fs.Length);
                }
                _lastEntries = (path, info.Length, info.LastWriteTimeUtc, entries);
                return entries;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    private static void AddEntries(JsonElement e, List<TranscriptEntryDto> entries)
    {
        var at = Time(e);
        switch (TranscriptReader.Str(e, "type"))
        {
            case "user" when IsUserPrompt(e):
                entries.Add(new TranscriptEntryDto { Role = "user", Text = PromptText(e), At = at });
                break;
            case "assistant" when !Sidechain(e) && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
                                  && m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array:
                foreach (var block in content.EnumerateArray())
                {
                    if (TranscriptReader.Str(block, "type") == "text" && TranscriptReader.Str(block, "text") is { Length: > 0 } text)
                        entries.Add(new TranscriptEntryDto { Role = "assistant", Text = text.Trim(), At = at });
                    else if (TranscriptReader.Str(block, "type") == "tool_use")
                        entries.Add(new TranscriptEntryDto { Role = "tool", Text = ToolLine(block), At = at });
                }
                break;
        }
    }

    private static string ToolLine(JsonElement block)
    {
        var name = TranscriptReader.Str(block, "name") ?? "a tool";
        block.TryGetProperty("input", out var input);
        return name switch
        {
            "Bash" or "PowerShell" when TranscriptReader.Str(input, "command") is { } cmd => "Ran " + AgentRegistry.Clip(OneLine(cmd), 80),
            "Edit" or "Write" or "MultiEdit" when TranscriptReader.Str(input, "file_path") is { } f => "Edited " + Path.GetFileName(f),
            _ => "Used " + name,
        };
    }

    private static bool Sidechain(JsonElement e) => e.TryGetProperty("isSidechain", out var s) && s.ValueKind == JsonValueKind.True;

    /// <summary>A prompt the user typed in the main session: not a tool result, a subagent's or a meta line.</summary>
    private static bool IsUserPrompt(JsonElement e) =>
        !Sidechain(e) && !(e.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
        && !(e.TryGetProperty("origin", out var origin) && TranscriptReader.Str(origin, "kind") == "task-notification")
        && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object && TranscriptReader.IsPrompt(e)
        && PromptText(e) is { Length: > 0 } text && !NotTyped.Any(p => text.StartsWith(p, StringComparison.Ordinal));

    /// <summary>User lines Claude Code writes itself: slash commands and their output, task notifications, reminders.</summary>
    private static readonly string[] NotTyped = ["<command-name>", "<command-message>", "<local-command-", "<task-notification>", "<system-reminder>"];

    private static string PromptText(JsonElement e)
    {
        var c = e.GetProperty("message").GetProperty("content");
        if (c.ValueKind == JsonValueKind.String) return c.GetString()!.Trim();
        return string.Join("\n\n", c.EnumerateArray().Where(b => TranscriptReader.Str(b, "type") == "text").Select(b => TranscriptReader.Str(b, "text") ?? "")).Trim();
    }

    private static DateTimeOffset? Time(JsonElement e) =>
        TranscriptReader.Str(e, "timestamp") is { } s && DateTimeOffset.TryParse(s, out var t) ? t : null;

    /// <summary>Calls <paramref name="see"/> with the line when it parses as a JSON object; anything else is skipped.</summary>
    private static void Parse(string line, Action<JsonElement> see)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }
        using (doc)
            if (doc.RootElement.ValueKind == JsonValueKind.Object) see(doc.RootElement);
    }
}
