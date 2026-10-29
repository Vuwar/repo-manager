using System.Text.Json;
using RepoManager.Contracts;

namespace RepoManager.Core.Agents;

/// <summary>
/// Agent Town XP for the user (answering prompts, getting sessions working again) and for each repo (turns its
/// sessions finish), saved next to registry.json. A missing or unreadable file starts from zero.
/// </summary>
public sealed class TownProgress
{
    public const string FileName = "town-progress.json";
    public const int AnswerXp = 10, FastAnswerBonus = 10, SendXp = 5, TurnXp = 10;
    public static readonly TimeSpan FastAnswer = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SendWindow = TimeSpan.FromMinutes(2);

    private sealed class FileShape
    {
        public int Player { get; set; }
        public Dictionary<string, int> Repos { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly FileShape _data;
    private readonly Dictionary<string, string> _lastState = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _sent = new(StringComparer.Ordinal);

    /// <summary>Raised after every award, outside the lock, with the new totals.</summary>
    public event Action<TownProgressDto, XpAwardDto>? Awarded;

    public TownProgress(string dataDir, Func<DateTimeOffset> clock)
    {
        _path = Path.Combine(dataDir, FileName);
        _clock = clock;
        _data = Load(_path);
    }

    /// <summary>Level n starts at 25·(n−1)·n XP: 0, 50, 150, 300, 500…</summary>
    public static LevelDto LevelOf(int xp)
    {
        var level = 1;
        while (Start(level + 1) <= xp) level++;
        return new LevelDto(xp, level, Start(level), Start(level + 1));
    }

    private static int Start(int level) => 25 * (level - 1) * level;

    /// <summary>The user answered a prompt from the town, <paramref name="promptAge"/> after it appeared.</summary>
    public XpAwardDto? Answered(string sessionId, TimeSpan promptAge) =>
        Award(new XpAwardDto("answer", AnswerXp + (promptAge <= FastAnswer ? FastAnswerBonus : 0), sessionId, null), repo: null);

    /// <summary>A town message reached the session; if it gets the session working soon, that earns XP.</summary>
    public void Sent(string sessionId)
    {
        lock (_gate) _sent[sessionId] = _clock();
    }

    /// <summary>Feed every session change: awards a finished turn to its repo, and a town message that got it working.</summary>
    public XpAwardDto? SessionChanged(AgentSessionDto s)
    {
        XpAwardDto? award = null;
        lock (_gate)
        {
            var had = _lastState.TryGetValue(s.Id, out var before);
            _lastState[s.Id] = s.State;
            if (had && before == s.State) return null;
            if (s.State == "working" && _sent.Remove(s.Id, out var at) && _clock() - at <= SendWindow)
                award = new XpAwardDto("send", SendXp, s.Id, null);
            else if (s.State == "done" && had && s.Project is { Length: > 0 } repo)
                award = new XpAwardDto("turn", TurnXp, s.Id, repo);
        }
        return award == null ? null : Award(award, award.Repo);
    }

    public void Forget(string sessionId)
    {
        lock (_gate)
        {
            _lastState.Remove(sessionId);
            _sent.Remove(sessionId);
        }
    }

    public TownProgressDto Snapshot()
    {
        lock (_gate) return SnapshotLocked();
    }

    private TownProgressDto SnapshotLocked() =>
        new(LevelOf(_data.Player), _data.Repos.ToDictionary(p => p.Key, p => LevelOf(p.Value), StringComparer.OrdinalIgnoreCase));

    private XpAwardDto Award(XpAwardDto award, string? repo)
    {
        TownProgressDto snapshot;
        lock (_gate)
        {
            if (repo != null) _data.Repos[repo] = _data.Repos.GetValueOrDefault(repo) + award.Amount;
            else _data.Player += award.Amount;
            Save();
            snapshot = SnapshotLocked();
        }
        Awarded?.Invoke(snapshot, award);
        return award;
    }

    /// <summary>Temp file then move, so a quit mid-write never leaves half a file.</summary>
    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Protocol.Json));
            File.Move(tmp, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // XP is only a game: keep it in memory and try again on the next award.
        }
    }

    private static FileShape Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new FileShape();
            var data = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Protocol.Json) ?? new FileShape();
            data.Repos = new Dictionary<string, int>(data.Repos ?? [], StringComparer.OrdinalIgnoreCase);
            return data;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new FileShape();
        }
    }
}
