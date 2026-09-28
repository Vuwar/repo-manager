using RepoManager.Contracts;

namespace RepoManager.Core.Config;

/// <summary>Lists the subfolders of a checkout for the folder picker in the config view.</summary>
public static class FolderLister
{
    /// <summary>Dependency, build and tool folders that never hold a service to start.</summary>
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "build", "out", "target", "coverage", "packages",
        "venv", "__pycache__", "wwwroot", "TestResults", "artifacts",
    };

    private static readonly (string Pattern, string Marker)[] Markers =
    [
        ("package.json", "package.json"),
        ("*.csproj", "csproj"),
        ("*.sln", "sln"),
        ("*.slnx", "sln"),
        ("pyproject.toml", "python"),
        ("go.mod", "go"),
        ("Cargo.toml", "rust"),
        ("pom.xml", "java"),
        ("build.gradle", "java"),
    ];

    /// <summary>
    /// Folders below <paramref name="root"/> up to <paramref name="maxDepth"/> levels, as "/"-separated relative
    /// paths. "." is the root itself. Hidden folders (".git", ".vs", ...) and <see cref="Skip"/> are left out.
    /// </summary>
    public static List<FolderDto> List(string root, int maxDepth = 4, int limit = 1000)
    {
        var result = new List<FolderDto>();
        if (!Directory.Exists(root)) return result;
        result.Add(new FolderDto(".", MarkersOf(root)));
        Walk(root, "", 1, maxDepth, limit, result);
        return result;
    }

    private static void Walk(string dir, string rel, int depth, int maxDepth, int limit, List<FolderDto> result)
    {
        if (depth > maxDepth) return;
        List<string> subs;
        try { subs = Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return; }
        foreach (var sub in subs)
        {
            if (result.Count >= limit) return;
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.') || Skip.Contains(name)) continue;
            try { if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; }
            catch { continue; }
            var path = rel.Length == 0 ? name : rel + "/" + name;
            result.Add(new FolderDto(path, MarkersOf(sub)));
            Walk(sub, path, depth + 1, maxDepth, limit, result);
        }
    }

    private static List<string> MarkersOf(string dir)
    {
        var found = new List<string>();
        foreach (var (pattern, marker) in Markers)
        {
            if (found.Contains(marker)) continue;
            try { if (Directory.EnumerateFiles(dir, pattern).Any()) found.Add(marker); }
            catch { /* unreadable folder: no markers */ }
        }
        return found;
    }
}
