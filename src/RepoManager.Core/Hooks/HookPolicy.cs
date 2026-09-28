using System.Text.Json;
using System.Text.RegularExpressions;
using RepoManager.Contracts;

namespace RepoManager.Core.Hooks;

/// <summary>What the hook needs to know about one managed service.</summary>
public sealed record ManagedServiceInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string InstanceKey { get; init; }
    public required string Root { get; init; }
    /// <summary>Full working directory of the service.</summary>
    public required string Cwd { get; init; }
    public bool Up { get; init; }
    public bool Active { get; init; }
    public int? Port { get; init; }
    public IReadOnlyCollection<int> Pids { get; init; } = [];
    /// <summary>Lower-case process names in the service tree, without ".exe".</summary>
    public IReadOnlyCollection<string> ProcessNames { get; init; } = [];
    public required string Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public string? Url { get; init; }
}

/// <summary>
/// Decides whether a Claude Code tool call would kill or duplicate a managed dev server.
/// Denies only on a clear match; anything uncertain is allowed.
/// </summary>
public static partial class HookPolicy
{
    public static HookDecisionDto Allow => new(false, null);

    public static HookDecisionDto Evaluate(HookRequest req, IReadOnlyList<ManagedServiceInfo> services)
    {
        try
        {
            using var input = string.IsNullOrWhiteSpace(req.ToolInputJson) ? null : JsonDocument.Parse(req.ToolInputJson);
            var root = input?.RootElement;
            switch (req.ToolName)
            {
                case "Bash":
                case "PowerShell":
                    var command = GetString(root, "command");
                    return string.IsNullOrWhiteSpace(command) ? Allow : EvaluateShell(command!, req.Cwd, services);
                case "mcp__Claude_Browser__preview_start":
                    return EvaluatePreview(GetString(root, "name"), GetString(root, "url"), req.Cwd, services);
                default:
                    return Allow;
            }
        }
        catch
        {
            return Allow;
        }
    }

    private static string? GetString(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---- preview_start ----

    private static HookDecisionDto EvaluatePreview(string? name, string? url, string? cwd, IReadOnlyList<ManagedServiceInfo> services)
    {
        if (string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(url)) return Allow;
        var inInstance = ServicesAt(cwd, services);
        var svc = inInstance.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (svc == null) return Allow;
        var how = svc.Up && svc.Url != null
            ? $"It is already running at {svc.Url}: call preview_start with url \"{svc.Url}\" instead of name."
            : $"Run `devm start {svc.Name}` first (it prints the URL), then call preview_start with that url instead of name.";
        return new HookDecisionDto(true,
            $"'{svc.Name}' is managed by RepoManager ({svc.Id}). preview_start with a name would launch a second copy from launch.json. {how}");
    }

    // ---- Shell commands ----

    private static readonly string[] KillWords = ["taskkill", "stop-process", "spps", "kill", "pkill", "killall", "kill-port", "fuser", "tskill"];

    private static HookDecisionDto EvaluateShell(string command, string? cwd, IReadOnlyList<ManagedServiceInfo> services)
    {
        var lower = command.ToLowerInvariant();
        var active = services.Where(s => s.Active).ToList();

        if (active.Count > 0 && KillWords.Any(w => ContainsWord(lower, w)))
        {
            var hit = KillTarget(lower, active);
            if (hit != null)
                return new HookDecisionDto(true,
                    $"This would kill {hit.Id}, which is managed by RepoManager. Do not kill dev servers directly. " +
                    $"Use `devm restart {hit.Id}` or `devm stop {hit.Id}` (MCP: restart_service / stop_service). Logs: `devm logs {hit.Id}`.");
        }

        var start = DirectStart(command, cwd, services);
        if (start != null)
        {
            var state = start.Up ? $"It is already running{(start.Url != null ? " at " + start.Url : "")}. " : "";
            return new HookDecisionDto(true,
                $"This starts {start.Id} directly, but it is managed by RepoManager. {state}" +
                $"Use `devm start {start.Name}` / `devm restart {start.Name}` from this folder (MCP: start_service / restart_service), then `devm logs {start.Name}`.");
        }
        return Allow;
    }

    /// <summary>Managed service whose PID, port or process name the kill command targets.</summary>
    private static ManagedServiceInfo? KillTarget(string lower, List<ManagedServiceInfo> active)
    {
        // PIDs: any number in the command that is a managed PID.
        foreach (Match m in Number().Matches(lower))
        {
            if (!int.TryParse(m.Value, out var n)) continue;
            var byPid = active.FirstOrDefault(s => s.Pids.Contains(n));
            if (byPid != null) return byPid;
        }
        // Ports: kill-port 5173, fuser -k 5173/tcp, Get-NetTCPConnection -LocalPort 5173 | Stop-Process, netstat ... :5173
        foreach (Match m in Number().Matches(lower))
        {
            if (!int.TryParse(m.Value, out var n)) continue;
            var byPort = active.FirstOrDefault(s => s.Port == n);
            if (byPort != null && (lower.Contains("kill-port") || lower.Contains("fuser") || lower.Contains("localport") ||
                                   lower.Contains(":" + n) || lower.Contains("netstat") || lower.Contains("get-nettcpconnection")))
                return byPort;
        }
        // Names: taskkill /IM node.exe, Stop-Process -Name node, Get-Process node | Stop-Process, pkill node, killall dotnet.
        foreach (var s in active)
            foreach (var name in s.ProcessNames)
            {
                if (name is "cmd" or "conhost") continue;
                if (NamePattern(name).IsMatch(lower)) return s;
            }
        return null;
    }

    private static Regex NamePattern(string name)
    {
        var n = Regex.Escape(name);
        return new Regex(
            $@"(/im\s+""?{n}(\.exe)?""?)|(-(process)?name\s+""?{n}(\.exe)?""?)|(get-process\s+(-name\s+)?""?{n}""?.*\|\s*(stop-process|spps|kill))|(\bps\s+""?{n}""?.*\|\s*(stop-process|spps|kill))|((pkill|killall|tskill)\s+(-\S+\s+)*""?{n}(\.exe)?""?)|(\bspps\s+-n(ame)?\s+{n})",
            RegexOptions.IgnoreCase);
    }

    private static ManagedServiceInfo? DirectStart(string command, string? cwd, IReadOnlyList<ManagedServiceInfo> services)
    {
        if (cwd == null) return null;
        var effectiveCwd = cwd;
        foreach (var segment in Segments(command))
        {
            var tokens = Tokenize(segment);
            if (tokens.Count == 0) continue;
            var exe = Path.GetFileNameWithoutExtension(tokens[0]).ToLowerInvariant();
            if (exe is "cd" or "set-location" or "sl" or "pushd" or "chdir")
            {
                var target = tokens.Skip(1).FirstOrDefault(t => !t.Equals("/d", StringComparison.OrdinalIgnoreCase) && !t.StartsWith('-'));
                if (target != null) effectiveCwd = Combine(effectiveCwd, target);
                continue;
            }
            if (exe is "devm" or "git") continue;

            foreach (var s in ServicesAt(effectiveCwd, services))
            {
                if (Matches(s, exe, tokens, effectiveCwd)) return s;
            }
        }
        return null;
    }

    private static bool Matches(ManagedServiceInfo s, string exe, List<string> tokens, string cwd)
    {
        var svcExe = Path.GetFileNameWithoutExtension(s.Command).ToLowerInvariant();
        var svcArgs = s.Args.Select(a => a.ToLowerInvariant()).ToList();
        var args = tokens.Skip(1).Select(a => a.ToLowerInvariant()).ToList();

        if (svcExe is "npm" or "pnpm" or "yarn" or "bun")
        {
            if (exe is not ("npm" or "pnpm" or "yarn" or "bun")) return false;
            var svcScript = Script(svcExe, svcArgs);
            var script = Script(exe, args);
            if (svcScript == null || script == null || svcScript != script) return false;
            var svcPrefix = Prefix(svcArgs);
            var prefix = Prefix(args);
            var svcDir = svcPrefix != null ? Combine(s.Cwd, svcPrefix) : s.Cwd;
            var dir = prefix != null ? Combine(cwd, prefix) : cwd;
            return SamePath(svcDir, dir);
        }

        if (svcExe == "dotnet")
        {
            if (exe != "dotnet" || args.Count == 0 || args[0] is not ("run" or "watch")) return false;
            var svcVerb = svcArgs.FirstOrDefault();
            if (svcVerb is not ("run" or "watch")) return false;
            var svcProject = Option(svcArgs, "--project", "-p");
            var project = Option(args, "--project", "-p");
            var svcDir = Combine(s.Cwd, svcProject ?? ".");
            var dir = Combine(cwd, project ?? ".");
            return SamePath(StripProjectFile(svcDir), StripProjectFile(dir));
        }

        // Anything else: same executable and same first argument, run from the service folder.
        if (exe != svcExe) return false;
        if (svcArgs.Count > 0 && (args.Count == 0 || args[0] != svcArgs[0])) return false;
        return SamePath(s.Cwd, cwd);
    }

    private static string? Script(string exe, List<string> args)
    {
        var rest = args.Where((a, i) => !a.StartsWith('-') && !(i > 0 && args[i - 1] is "--prefix" or "-c" or "--cwd" or "--dir" or "-C")).ToList();
        if (rest.Count == 0) return null;
        if (rest[0] is "run" or "run-script") return rest.Count > 1 ? rest[1] : null;
        if (exe == "npm" && rest[0] is "start" or "test") return rest[0];
        if (exe != "npm") return rest[0];
        return null;
    }

    private static string? Prefix(List<string> args) => Option(args, "--prefix", "-c", "--cwd", "--dir", "-C");

    private static string? Option(List<string> args, params string[] names)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--") break;
            foreach (var n in names)
            {
                if (args[i] == n && i + 1 < args.Count) return args[i + 1];
                if (args[i].StartsWith(n + "=")) return args[i][(n.Length + 1)..];
            }
        }
        return null;
    }

    private static string StripProjectFile(string p) =>
        p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(p) ?? p : p;

    private static string Combine(string baseDir, string rel)
    {
        try { return PathUtil.Normalize(Path.Combine(baseDir, PathUtil.FromPosix(rel.Trim('"', '\'')))); }
        catch { return baseDir; }
    }

    private static bool SamePath(string a, string b) => PathUtil.Same(a, b);

    /// <summary>Services of the deepest instance containing cwd.</summary>
    private static List<ManagedServiceInfo> ServicesAt(string? cwd, IReadOnlyList<ManagedServiceInfo> services)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return [];
        var best = services
            .Where(s => PathUtil.IsUnder(cwd, s.Root))
            .GroupBy(s => s.InstanceKey)
            .OrderByDescending(g => g.First().Root.Length)
            .FirstOrDefault();
        return best?.ToList() ?? [];
    }

    /// <summary>Splits on ; &amp;&amp; || | and newlines, so "cd web &amp;&amp; npm run dev" yields both commands.</summary>
    private static IEnumerable<string> Segments(string command) =>
        SegmentSplit().Split(command).Select(s => s.Trim()).Where(s => s.Length > 0);

    private static List<string> Tokenize(string segment)
    {
        var tokens = new List<string>();
        foreach (Match m in TokenPattern().Matches(segment))
            tokens.Add(m.Value.Trim('"', '\''));
        // Skip env assignments ("PORT=1 npm run dev") and call operators ("& npm").
        while (tokens.Count > 0 && (tokens[0] == "&" || (tokens[0].Contains('=') && !tokens[0].StartsWith('-')))) tokens.RemoveAt(0);
        return tokens;
    }

    private static bool ContainsWord(string text, string word) => Regex.IsMatch(text, $@"(^|[^a-z0-9_-]){Regex.Escape(word)}($|[^a-z0-9_-])");

    [GeneratedRegex(@"\b\d{2,6}\b")]
    private static partial Regex Number();

    [GeneratedRegex(@"\s*(?:&&|\|\||;|\r?\n|\|)\s*")]
    private static partial Regex SegmentSplit();

    [GeneratedRegex(@"""[^""]*""|'[^']*'|\S+")]
    private static partial Regex TokenPattern();
}
