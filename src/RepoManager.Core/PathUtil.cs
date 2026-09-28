using System.Runtime.InteropServices;
using System.Text;

namespace RepoManager.Core;

public static class PathUtil
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferSize);

    /// <summary>Full path, long form (C:\Users\VUQAR~1.KAR → C:\Users\vuqar.karimli), Git Bash form converted, no trailing separator.</summary>
    public static string Normalize(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return p;
        p = FromPosix(p.Trim().Trim('"')).Replace('/', '\\');
        try { p = Path.GetFullPath(p); } catch { return p.TrimEnd('\\'); }
        if (p.Contains('~')) p = LongForm(p);
        return p.Length > 3 ? p.TrimEnd('\\') : p;
    }

    private static string LongForm(string p)
    {
        var sb = new StringBuilder(1024);
        var n = GetLongPathNameW(p, sb, (uint)sb.Capacity);
        if (n > 0 && n < sb.Capacity) return sb.ToString();
        // Path may not exist yet: expand the longest existing parent.
        var parent = Path.GetDirectoryName(p);
        return parent != null && parent != p ? Path.Combine(LongForm(parent), Path.GetFileName(p)) : p;
    }

    /// <summary>Git Bash paths: "/c/Users/x" → "C:\Users\x".</summary>
    public static string FromPosix(string p)
    {
        if (p.Length >= 2 && p[0] == '/' && char.IsLetter(p[1]) && (p.Length == 2 || p[2] == '/'))
            return char.ToUpperInvariant(p[1]) + ":\\" + (p.Length > 3 ? p[3..].Replace('/', '\\') : "");
        return p;
    }

    public static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    public static bool IsUnder(string path, string root)
    {
        var p = Normalize(path) + "\\";
        var r = Normalize(root).TrimEnd('\\') + "\\";
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
