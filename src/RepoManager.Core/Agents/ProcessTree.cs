using System.Runtime.InteropServices;

namespace RepoManager.Core.Agents;

/// <summary>Parent-process lookup from one Toolhelp snapshot (a few milliseconds).</summary>
public static class ProcessTree
{
    private const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static Dictionary<int, (int Parent, string Name)> Snapshot()
    {
        var map = new Dictionary<int, (int, string)>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (var ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                map[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, e.szExeFile);
        }
        finally
        {
            CloseHandle(snap);
        }
        return map;
    }

    /// <summary>Parents of <paramref name="pid"/>, nearest first, as (pid, exe file name). Stops at pid 0 or a cycle.</summary>
    public static IReadOnlyList<(int Pid, string Name)> Ancestors(int pid)
    {
        var map = Snapshot();
        var list = new List<(int, string)>();
        var seen = new HashSet<int> { pid };
        var current = pid;
        while (map.TryGetValue(current, out var entry) && entry.Parent != 0 && seen.Add(entry.Parent)
               && map.TryGetValue(entry.Parent, out var parent))
        {
            list.Add((entry.Parent, parent.Name));
            current = entry.Parent;
        }
        return list;
    }

    /// <summary>When the process started, or null when it is gone (or not ours to read). Tells a live pid from a reused one.</summary>
    public static DateTime? StartTime(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The nearest ancestor whose exe name matches, or null.</summary>
    public static int? FindAncestor(int pid, Func<string, bool> nameMatches)
    {
        foreach (var (p, name) in Ancestors(pid))
            if (nameMatches(name)) return p;
        return null;
    }
}
