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
    /// <summary>XP is clamped to 0..MaxXp, so a hand-edited file cannot overflow the level maths.</summary>
    public const int MaxXp = 1_000_000;

    private sealed class FileShape
    {
        public int Player { get; set; }
        /// <summary>Saved so it keeps growing across restarts (an open town keeps the snapshot with the higher one).</summary>
        public long Revision { get; set; }
        public Dictionary<string, int> Repos { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly FileShape _data;
    private readonly Dictionary<string, string> _lastState = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _sent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastSeq = new(StringComparer.Ordinal);

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
        xp = Math.Clamp(xp, 0, MaxXp);
        var level = 1;
        while (Start(level + 1) <= xp) level++;
        return new LevelDto(xp, level, (int)Start(level), (int)Start(level + 1));
    }

    private static long Start(int level) => 25L * (level - 1) * level;

    /// <summary>The user answered a prompt from the town, <paramref name="promptAge"/> after it appeared.</summary>
    public XpAwardDto? Answered(string sessionId, TimeSpan promptAge) =>
        Award(new XpAwardDto("answer", AnswerXp + (promptAge <= FastAnswer ? FastAnswerBonus : 0), sessionId, null), repo: null);

    /// <summary>A town message reached the session; if it gets the session working soon, that earns XP.</summary>
    public void Sent(string sessionId)
    {
        // Only a message that can start a turn counts: one read mid-turn does not get the session working.
        lock (_gate)
            if (_lastState.GetValueOrDefault(sessionId) != "working") _sent[sessionId] = _clock();
    }

    /// <summary>Feed every session change: awards a finished turn to its repo, and a town message that got it working.</summary>
    public XpAwardDto? SessionChanged(AgentSessionDto s)
    {
        XpAwardDto? award = null;
        lock (_gate)
        {
            // An update overtaken by a newer one for the same session is old news.
            if (s.Seq != 0 && _lastSeq.TryGetValue(s.Id, out var lastSeq) && s.Seq <= lastSeq) return null;
            if (s.Seq != 0) _lastSeq[s.Id] = s.Seq;
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
            _lastSeq.Remove(sessionId);
        }
    }

    public TownProgressDto Snapshot()
    {
        lock (_gate) return SnapshotLocked();
    }

    private TownProgressDto SnapshotLocked() =>
        new(LevelOf(_data.Player), _data.Repos.ToDictionary(p => p.Key, p => LevelOf(p.Value), StringComparer.OrdinalIgnoreCase), _data.Revision);

    private XpAwardDto Award(XpAwardDto award, string? repo)
    {
        TownProgressDto snapshot;
        lock (_gate)
        {
            if (repo != null) _data.Repos[repo] = Math.Min(MaxXp, _data.Repos.GetValueOrDefault(repo) + award.Amount);
            else _data.Player = Math.Min(MaxXp, _data.Player + award.Amount);
            _data.Revision++;
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
            data.Player = Math.Clamp(data.Player, 0, MaxXp);
            data.Repos = (data.Repos ?? []).ToDictionary(p => p.Key, p => Math.Clamp(p.Value, 0, MaxXp), StringComparer.OrdinalIgnoreCase);
            return data;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new FileShape();
        }
    }
}
