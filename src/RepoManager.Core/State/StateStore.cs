using System.Text.Json;
using RepoManager.Contracts;

namespace RepoManager.Core.State;

public sealed class StateFile
{
    /// <summary>Service ids the user wants running. A user stop removes the id; a crash keeps it.</summary>
    public List<string> Desired { get; set; } = [];

    /// <summary>Instance key → service name → port. Sticky auto-assigned ports.</summary>
    public Dictionary<string, Dictionary<string, int>> Ports { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class StateStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private StateFile _state;

    public StateStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "state.json");
        _state = Load();
    }

    private StateFile Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_path), Protocol.Json) ?? new StateFile();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new StateFile();
    }

    public IReadOnlyList<string> Desired { get { lock (_lock) return [.. _state.Desired]; } }

    public bool IsDesired(string id)
    {
        lock (_lock) return _state.Desired.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    public void SetDesired(string id, bool desired)
    {
        lock (_lock)
        {
            var has = _state.Desired.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            if (desired && has < 0) _state.Desired.Add(id);
            else if (!desired && has >= 0) _state.Desired.RemoveAt(has);
            else return;
            Save();
        }
    }

    public int? GetPort(string instanceKey, string service)
    {
        lock (_lock)
            return _state.Ports.TryGetValue(instanceKey, out var m) && m.TryGetValue(service, out var p) ? p : null;
    }

    public void SetPort(string instanceKey, string service, int port)
    {
        lock (_lock)
        {
            if (!_state.Ports.TryGetValue(instanceKey, out var m))
                _state.Ports[instanceKey] = m = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (m.TryGetValue(service, out var old) && old == port) return;
            m[service] = port;
            Save();
        }
    }

    /// <summary>All sticky ports, so the allocator never hands one out twice.</summary>
    public HashSet<int> AllStickyPorts()
    {
        lock (_lock) return _state.Ports.Values.SelectMany(m => m.Values).ToHashSet();
    }

    /// <summary>Drops sticky ports of instances that no longer exist (removed worktrees).</summary>
    public void Prune(IEnumerable<string> liveInstanceKeys)
    {
        var live = liveInstanceKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            var dead = _state.Ports.Keys.Where(k => !live.Contains(k)).ToList();
            if (dead.Count == 0) return;
            foreach (var k in dead) _state.Ports.Remove(k);
            Save();
        }
    }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true }));
            File.Move(tmp, _path, true);
        }
        catch (IOException) { /* next save retries */ }
    }
}
