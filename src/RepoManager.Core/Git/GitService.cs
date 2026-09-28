using System.Collections.Concurrent;
using System.Diagnostics;
using RepoManager.Contracts;

namespace RepoManager.Core.Git;

public sealed record WorktreeInfo(string Path, string Name, string? Branch);

/// <summary>Thin wrapper over the git CLI. Results are cached briefly because the UI polls.</summary>
public sealed class GitService
{
    private readonly ConcurrentDictionary<string, (DateTime At, GitInfoDto? Info)> _infoCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

    public GitInfoDto? GetInfo(string root, bool refresh = false)
    {
        if (!refresh && _infoCache.TryGetValue(root, out var c) && DateTime.UtcNow - c.At < CacheFor) return c.Info;
        var info = ReadInfo(root);
        _infoCache[root] = (DateTime.UtcNow, info);
        return info;
    }

    private static GitInfoDto? ReadInfo(string root)
    {
        var r = Run(root, "status", "--porcelain=v1", "-b", "--untracked-files=normal");
        if (r == null || r.Value.ExitCode != 0) return null;
        return ParseStatus(r.Value.Output);
    }

    /// <summary>Parses `git status --porcelain=v1 -b` output.</summary>
    public static GitInfoDto ParseStatus(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        if (lines.Count > 0 && lines[0].StartsWith("## "))
        {
            var head = lines[0][3..];
            var bracket = head.IndexOf(" [", StringComparison.Ordinal);
            var track = bracket >= 0 ? head[(bracket + 2)..].TrimEnd(']') : null;
            if (bracket >= 0) head = head[..bracket];
            var dots = head.IndexOf("...", StringComparison.Ordinal);
            if (dots >= 0) { branch = head[..dots]; upstream = head[(dots + 3)..]; }
            else branch = head;
            if (branch.StartsWith("No commits yet on ")) branch = branch["No commits yet on ".Length..];
            if (branch.StartsWith("HEAD (no branch)")) branch = "(detached)";
            if (track != null)
                foreach (var part in track.Split(',', StringSplitOptions.TrimEntries))
                {
                    if (part.StartsWith("ahead ") && int.TryParse(part[6..], out var a)) ahead = a;
                    else if (part.StartsWith("behind ") && int.TryParse(part[7..], out var b)) behind = b;
                }
            lines.RemoveAt(0);
        }
        return new GitInfoDto { Branch = branch, Upstream = upstream, Ahead = ahead, Behind = behind, Dirty = lines.Count > 0 };
    }

    /// <summary>Worktrees of the repo at <paramref name="root"/>, excluding the checkout at root itself.</summary>
    public IReadOnlyList<WorktreeInfo> ListWorktrees(string root)
    {
        var r = Run(root, "worktree", "list", "--porcelain");
        if (r == null || r.Value.ExitCode != 0) return [];
        return ParseWorktrees(r.Value.Output, root);
    }

    public static List<WorktreeInfo> ParseWorktrees(string output, string root)
    {
        var result = new List<WorktreeInfo>();
        string? path = null, branch = null;
        bool skip = false;

        void Flush()
        {
            if (path != null && !skip && !SamePath(path, root) && Directory.Exists(path))
            {
                var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
                // Names must stay unique inside a project: add a suffix on collision.
                var unique = name; var i = 2;
                while (result.Any(w => string.Equals(w.Name, unique, StringComparison.OrdinalIgnoreCase))) unique = $"{name}-{i++}";
                result.Add(new WorktreeInfo(System.IO.Path.GetFullPath(path), unique, branch));
            }
            path = null; branch = null; skip = false;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("worktree ")) { Flush(); path = line[9..].Replace('/', '\\'); }
            else if (line.StartsWith("branch ")) branch = line[7..].Replace("refs/heads/", "");
            else if (line == "bare" || line.StartsWith("prunable")) skip = true;
        }
        Flush();
        return result;
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string p)
    {
        try { p = System.IO.Path.GetFullPath(p.Replace('/', '\\')); } catch { /* keep as is */ }
        return p.TrimEnd('\\');
    }

    private static (int ExitCode, string Output)? Run(string cwd, params string[] args)
    {
        if (!Directory.Exists(cwd)) return null;
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return null; }
            return (p.ExitCode, stdout.Result);
        }
        catch
        {
            return null;
        }
    }
}
