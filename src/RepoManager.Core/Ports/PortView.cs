using RepoManager.Contracts;

namespace RepoManager.Core.Ports;

/// <summary>Builds the ports list and decides which rows the default "dev ports" view shows.</summary>
public static class PortView
{
    public const int DynamicRangeStart = 49152;

    public static List<PortDto> Build(
        IReadOnlyList<ListeningPort> listening,
        IReadOnlyDictionary<int, string> managed,
        Func<int, ProcessDetails> details,
        Func<int, string?> processName,
        IReadOnlyDictionary<int, string> commandLines,
        IEnumerable<string> hiddenNames,
        int selfPid)
    {
        var hidden = new HashSet<string>(hiddenNames, StringComparer.OrdinalIgnoreCase);
        var detailCache = new Dictionary<int, ProcessDetails>();
        var nameCache = new Dictionary<int, string?>();
        // Conflict: two different processes listening on one port.
        var conflictPorts = listening.GroupBy(l => l.Port).Where(g => g.Select(x => x.Pid).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();

        return listening
            .GroupBy(l => (l.Port, l.Pid))
            .Select(g =>
            {
                var pid = g.Key.Pid;
                if (!detailCache.TryGetValue(pid, out var d)) detailCache[pid] = d = details(pid);
                if (!nameCache.TryGetValue(pid, out var name)) nameCache[pid] = name = processName(pid);
                var managedBy = managed.GetValueOrDefault(pid);
                var isHidden = name != null && hidden.Contains(name);
                var self = pid == selfPid;
                var dev = managedBy != null ||
                          (d.Mine && !d.System && !isHidden && !self && g.Key.Port < DynamicRangeStart);
                return new PortDto
                {
                    Port = g.Key.Port,
                    Pid = pid,
                    ProcessName = name,
                    ProcessPath = d.Path,
                    CommandLine = commandLines.GetValueOrDefault(pid),
                    ManagedBy = managedBy,
                    Address = string.Join(", ", g.Select(x => x.Address).Distinct()),
                    Mine = d.Mine,
                    System = d.System,
                    Hidden = isHidden,
                    Dev = dev,
                    Conflict = conflictPorts.Contains(g.Key.Port),
                    CanKill = d.Mine && !d.System && managedBy == null && !self,
                };
            })
            .OrderBy(p => p.Port)
            .ThenBy(p => p.Pid)
            .ToList();
    }
}
