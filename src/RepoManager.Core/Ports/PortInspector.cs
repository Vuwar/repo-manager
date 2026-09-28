using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RepoManager.Core.Ports;

public sealed record ListeningPort(int Port, int Pid, string Address);

/// <summary>Lists listening TCP ports with their owning PID (GetExtendedTcpTable).</summary>
public static class PortInspector
{
    private const int AF_INET = 2, AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

    public static IReadOnlyList<ListeningPort> Listening()
    {
        var result = new List<ListeningPort>();
        Read(AF_INET, result);
        Read(AF_INET6, result);
        return result.DistinctBy(p => (p.Port, p.Pid, p.Address)).OrderBy(p => p.Port).ToList();
    }

    public static IReadOnlyList<ListeningPort> OwnersOf(int port) => Listening().Where(p => p.Port == port).ToList();

    public static bool IsListening(int port) => Listening().Any(p => p.Port == port);

    /// <summary>True when nothing listens on the port and it can be bound on loopback and any address.</summary>
    public static bool IsFree(int port)
    {
        if (IsListening(port)) return false;
        try
        {
            using var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false; // excluded range (Hyper-V/WSL reservations) or in use
        }
    }

    private static void Read(int family, List<ListeningPort> result)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var rc = GetExtendedTcpTable(buf, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (rc == 122 /* ERROR_INSUFFICIENT_BUFFER */) continue;
                if (rc != 0) return;
                var count = Marshal.ReadInt32(buf);
                if (family == AF_INET)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 x DWORD)
                    for (var i = 0; i < count; i++)
                    {
                        var row = buf + 4 + i * 24;
                        var addr = new IPAddress((uint)Marshal.ReadInt32(row, 4));
                        var port = PortFromNetwork(Marshal.ReadInt32(row, 8));
                        var pid = Marshal.ReadInt32(row, 20);
                        result.Add(new ListeningPort(port, pid, addr.ToString()));
                    }
                }
                else
                {
                    // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScopeId, localPort, remoteAddr[16], remoteScopeId, remotePort, state, pid
                    for (var i = 0; i < count; i++)
                    {
                        var row = buf + 4 + i * 56;
                        var bytes = new byte[16];
                        Marshal.Copy(row, bytes, 0, 16);
                        var port = PortFromNetwork(Marshal.ReadInt32(row, 20));
                        var pid = Marshal.ReadInt32(row, 52);
                        result.Add(new ListeningPort(port, pid, "[" + new IPAddress(bytes) + "]"));
                    }
                }
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    private static int PortFromNetwork(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    public static string? ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }

    public static string? CommandLine(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (var o in searcher.Get()) return o["CommandLine"] as string;
        }
        catch { }
        return null;
    }

    /// <summary>Kills an external port owner and its children (used for --kill-owner).</summary>
    public static void KillTree(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
        catch { }
    }
}

/// <summary>Picks ports from 20000–29999, avoiding busy ports and ports already given out.</summary>
public sealed class PortAllocator
{
    public const int RangeStart = 20000, RangeEnd = 29999;
    private readonly Func<int, bool> _isFree;

    public PortAllocator(Func<int, bool>? isFree = null) => _isFree = isFree ?? PortInspector.IsFree;

    public int? Allocate(ISet<int> taken, int? preferred = null)
    {
        if (preferred is >= RangeStart and <= RangeEnd && !taken.Contains(preferred.Value) && _isFree(preferred.Value))
            return preferred;
        // Start at a random offset so parallel daemons (tests) rarely race for the same port.
        var span = RangeEnd - RangeStart + 1;
        var offset = Random.Shared.Next(span);
        for (var i = 0; i < span; i++)
        {
            var p = RangeStart + (offset + i) % span;
            if (!taken.Contains(p) && _isFree(p)) return p;
        }
        return null;
    }
}
