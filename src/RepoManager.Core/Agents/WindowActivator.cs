using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RepoManager.Core.Agents;

/// <summary>Brings a Claude session's own window to the front.</summary>
public static partial class WindowActivator
{
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    // The desktop app's own deep-link check (claude://code/continue?session=...), from its app bundle.
    [GeneratedRegex("^local_[A-Za-z0-9-]{1,64}$")]
    private static partial Regex HostSessionId();

    /// <summary>
    /// The Claude desktop link that opens this session (CLAUDE_CODE_HOST_SESSION_ID), or null when the id is not one.
    /// Undocumented: found in the desktop app's link handler. A failed open falls back to the window.
    /// </summary>
    public static string? DesktopLink(string? hostSessionId) =>
        hostSessionId != null && HostSessionId().IsMatch(hostSessionId) ? "claude://code/continue?session=" + hostSessionId : null;

    /// <summary>Opens a desktop link with the shell. Never throws.</summary>
    public static bool OpenLink(string link)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // Climbing past these would bring up an unrelated window (a classic console's conhost is a sibling, not a
    // parent, so the chain reaches the desktop shell) or RepoManager itself.
    private static readonly HashSet<string> StopAt = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "RepoManager.exe", "services.exe", "svchost.exe", "wininit.exe", "winlogon.exe", "sihost.exe", "userinit.exe",
    };

    /// <summary>Pid of the first process in the chain (the session's process, then its parents) that has a window, stopping at the desktop shell and system processes.</summary>
    public static int? WindowOwner(IEnumerable<(int Pid, string Name, bool HasWindow)> chain)
    {
        foreach (var (pid, name, hasWindow) in chain)
        {
            if (StopAt.Contains(name)) return null;
            if (hasWindow) return pid;
        }
        return null;
    }

    /// <summary>Activates the window of <paramref name="pid"/> or its nearest parent that has one. Never throws.</summary>
    public static (bool Ok, string Message) Activate(int pid)
    {
        var handles = new Dictionary<int, IntPtr>();
        IEnumerable<(int, string, bool)> Chain()
        {
            foreach (var (p, name) in new[] { (pid, NameOf(pid)) }.Concat(ProcessTree.Ancestors(pid)))
            {
                var hwnd = WindowOf(p);
                handles[p] = hwnd;
                yield return (p, name, hwnd != IntPtr.Zero);
            }
        }
        if (WindowOwner(Chain()) is not { } owner)
            return (false, "this session's window was not found; it may run in a terminal RepoManager cannot reach");
        var h = handles[owner];
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        var label = NameOf(owner);
        return SetForegroundWindow(h) ? (true, "opened " + label) : (false, "Windows did not let RepoManager bring " + label + " to the front");
    }

    private static IntPtr WindowOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainWindowHandle;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return IntPtr.Zero;
        }
    }

    private static string NameOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "";
        }
    }
}
