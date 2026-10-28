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

    /// <summary>Activates the first visible top-level window of <paramref name="pid"/> or its ancestors. Never throws.</summary>
    public static (bool Ok, string Message) Activate(int pid)
    {
        foreach (var candidate in new[] { pid }.Concat(ProcessTree.Ancestors(pid).Select(a => a.Pid)))
        {
            IntPtr hwnd;
            string name;
            try
            {
                using var p = Process.GetProcessById(candidate);
                hwnd = p.MainWindowHandle;
                name = p.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                continue;
            }
            if (hwnd == IntPtr.Zero) continue;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            return SetForegroundWindow(hwnd)
                ? (true, "opened " + name)
                : (false, "Windows did not let RepoManager bring " + name + " to the front");
        }
        return (false, "this session's window was not found; it may run in a terminal RepoManager cannot reach");
    }
}
