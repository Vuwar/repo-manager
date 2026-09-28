using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RepoManager.Core.Ports;

public sealed record ProcessDetails(string? Path, bool Mine, bool System);

/// <summary>Owner and image path of a process, with only PROCESS_QUERY_LIMITED_INFORMATION rights.</summary>
public static class ProcessInfo
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenUser = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle h, int flags, StringBuilder name, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle h, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int cls, IntPtr info, int length, out int returned);

    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly string WinDir = (Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\");

    public static ProcessDetails Get(int pid)
    {
        if (pid is 0 or 4) return new ProcessDetails(null, false, true);
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return new ProcessDetails(null, false, true); // protected or another account

        string? path = null;
        var sb = new StringBuilder(1024);
        var size = sb.Capacity;
        if (QueryFullProcessImageNameW(h, 0, sb, ref size)) path = sb.ToString();

        var mine = false;
        if (OpenProcessToken(h, TOKEN_QUERY, out var token))
        {
            using (token)
            {
                GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var needed);
                if (needed > 0)
                {
                    var buf = Marshal.AllocHGlobal(needed);
                    try
                    {
                        if (GetTokenInformation(token, TokenUser, buf, needed, out _))
                            mine = new SecurityIdentifier(Marshal.ReadIntPtr(buf)) == Me;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
        }

        var system = !mine || (path != null && path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase));
        return new ProcessDetails(path, mine, system);
    }
}
