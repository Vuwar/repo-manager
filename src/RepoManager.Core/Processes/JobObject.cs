using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RepoManager.Core.Processes;

/// <summary>
/// Windows Job Object with KILL_ON_JOB_CLOSE. Only the daemon holds the handle, so when the daemon exits
/// for any reason (including a crash or taskkill /F) Windows closes the handle and kills every process in the job.
/// </summary>
public sealed class JobObject : IDisposable
{
    private readonly SafeFileHandle _handle;

    public JobObject()
    {
        _handle = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!Native.SetInformationJobObject(_handle, Native.JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(info)))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");
    }

    public void Assign(IntPtr processHandle)
    {
        if (!Native.AssignProcessToJobObject(_handle, processHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
    }

    /// <summary>Kills every process in the job, including grandchildren whose parent already exited.</summary>
    public void Terminate(uint exitCode = 1)
    {
        try { if (!_handle.IsClosed) Native.TerminateJobObject(_handle, exitCode); }
        catch (ObjectDisposedException) { /* closed concurrently: KILL_ON_JOB_CLOSE already did it */ }
    }

    /// <summary>PIDs of all live processes in the job. Empty once the job is closed.</summary>
    public IReadOnlyList<int> ProcessIds()
    {
        try { return QueryProcessIds(); }
        catch (ObjectDisposedException) { return []; }
    }

    private IReadOnlyList<int> QueryProcessIds()
    {
        if (_handle.IsClosed) return [];
        var capacity = 64;
        while (true)
        {
            var size = 8 + IntPtr.Size * capacity;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (Native.QueryInformationJobObject(_handle, Native.JobObjectBasicProcessIdList, buf, size, out _))
                {
                    var count = Marshal.ReadInt32(buf, 4);
                    var result = new int[count];
                    for (var i = 0; i < count; i++) result[i] = (int)Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size);
                    return result;
                }
                if (Marshal.GetLastWin32Error() != 234 /* ERROR_MORE_DATA */ || capacity > 65536) return [];
                capacity *= 4;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    public void Dispose() => _handle.Dispose();
}
