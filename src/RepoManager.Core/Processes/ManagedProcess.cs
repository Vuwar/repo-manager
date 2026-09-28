using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RepoManager.Contracts;

namespace RepoManager.Core.Processes;

public sealed class ProcessSpec
{
    /// <summary>Command line run through cmd.exe, so npm.cmd, .bat and shell builtins work.</summary>
    public required string CommandLine { get; init; }
    public required string WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// A process tree started through cmd.exe inside its own Job Object. The process is created suspended and
/// joins the job before it runs a single instruction, so no child can escape. Only the three pipe handles are
/// inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), so concurrent starts never leak each other's pipes.
/// </summary>
public sealed class ManagedProcess : IDisposable
{
    private readonly JobObject _job;
    private readonly IntPtr _processHandle;
    private readonly IntPtr _stdinWrite;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RegisteredWaitHandle _wait;
    private readonly ProcessWaitHandle _waitHandle;
    private int _disposed;

    public int Pid { get; }
    public Task<int> Exited => _exited.Task;

    private ManagedProcess(JobObject job, Native.PROCESS_INFORMATION pi, IntPtr stdinWrite)
    {
        _job = job;
        _processHandle = pi.hProcess;
        _stdinWrite = stdinWrite;
        Pid = pi.dwProcessId;
        _waitHandle = new ProcessWaitHandle(pi.hProcess);
        _wait = ThreadPool.RegisterWaitForSingleObject(_waitHandle, (_, _) =>
        {
            Native.GetExitCodeProcess(_processHandle, out var code);
            _exited.TrySetResult(unchecked((int)code));
        }, null, Timeout.Infinite, true);
    }

    public IReadOnlyList<int> TreePids() => _job.ProcessIds();

    /// <summary>Kills the whole tree and waits for the root process to exit.</summary>
    public async Task KillAsync(TimeSpan? timeout = null)
    {
        var pids = _job.ProcessIds();
        _job.Terminate();
        await Task.WhenAny(Exited, Task.Delay(timeout ?? TimeSpan.FromSeconds(10)));
        // Termination of the other tree members is asynchronous; wait until they are gone so ports and files are free.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until && (_job.ProcessIds().Count > 0 || pids.Any(IsAlive))) await Task.Delay(50);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public static ManagedProcess Start(ProcessSpec spec, Action<LogStream, string> onLine)
    {
        var sa = new Native.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
        IntPtr outRead = 0, outWrite = 0, errRead = 0, errWrite = 0, inRead = 0, inWrite = 0;
        IntPtr attrList = 0, handleArray = 0, env = 0;
        var job = new JobObject();
        var ok = false;
        try
        {
            if (!Native.CreatePipe(out outRead, out outWrite, ref sa, 0) ||
                !Native.CreatePipe(out errRead, out errWrite, ref sa, 0) ||
                !Native.CreatePipe(out inRead, out inWrite, ref sa, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");
            // Parent ends must not be inheritable.
            Native.SetHandleInformation(outRead, Native.HANDLE_FLAG_INHERIT, 0);
            Native.SetHandleInformation(errRead, Native.HANDLE_FLAG_INHERIT, 0);
            Native.SetHandleInformation(inWrite, Native.HANDLE_FLAG_INHERIT, 0);

            var size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
            handleArray = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handleArray, 0, inRead);
            Marshal.WriteIntPtr(handleArray, IntPtr.Size, outWrite);
            Marshal.WriteIntPtr(handleArray, IntPtr.Size * 2, errWrite);
            if (!Native.UpdateProcThreadAttribute(attrList, 0, Native.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleArray, IntPtr.Size * 3, 0, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

            var si = new Native.STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            si.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = inRead;
            si.StartupInfo.hStdOutput = outWrite;
            si.StartupInfo.hStdError = errWrite;
            si.lpAttributeList = attrList;

            env = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(spec.Environment));
            var comspec = System.Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            // chcp 65001: .NET console apps then write UTF-8, like node does.
            var cmdLine = new StringBuilder($"\"{comspec}\" /d /s /c \"chcp 65001 >nul & {spec.CommandLine}\"");
            var flags = Native.CREATE_SUSPENDED | Native.CREATE_NO_WINDOW | Native.CREATE_UNICODE_ENVIRONMENT |
                        Native.CREATE_NEW_PROCESS_GROUP | Native.EXTENDED_STARTUPINFO_PRESENT;

            if (!Directory.Exists(spec.WorkingDirectory))
                throw new DirectoryNotFoundException($"working directory not found: {spec.WorkingDirectory}");
            if (!Native.CreateProcessW(comspec, cmdLine, 0, 0, true, flags, env, spec.WorkingDirectory, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");

            try
            {
                job.Assign(pi.hProcess);
            }
            catch
            {
                Native.CloseHandle(pi.hThread);
                using (var h = new SafeProcessHandle(pi.hProcess, true)) { NativeTerminate(h); }
                throw;
            }

            // Child ends belong to the child now.
            Native.CloseHandle(inRead); inRead = 0;
            Native.CloseHandle(outWrite); outWrite = 0;
            Native.CloseHandle(errWrite); errWrite = 0;

            var mp = new ManagedProcess(job, pi, inWrite);
            StartReader(outRead, LogStream.Out, onLine);
            StartReader(errRead, LogStream.Err, onLine);
            outRead = 0; errRead = 0;
            Native.ResumeThread(pi.hThread);
            Native.CloseHandle(pi.hThread);
            ok = true;
            return mp;
        }
        finally
        {
            if (attrList != 0) { Native.DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (handleArray != 0) Marshal.FreeHGlobal(handleArray);
            if (env != 0) Marshal.FreeHGlobal(env);
            foreach (var h in new[] { outRead, outWrite, errRead, errWrite, inRead })
                if (h != 0) Native.CloseHandle(h);
            if (!ok)
            {
                if (inWrite != 0) Native.CloseHandle(inWrite);
                job.Dispose();
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(SafeProcessHandle h, uint code);

    private static void NativeTerminate(SafeProcessHandle h) => TerminateProcess(h, 1);

    private static void StartReader(IntPtr handle, LogStream stream, Action<LogStream, string> onLine)
    {
        var thread = new Thread(() =>
        {
            using var fs = new FileStream(new SafeFileHandle(handle, true), FileAccess.Read, 1, false);
            var decoder = Encoding.UTF8.GetDecoder();
            var bytes = new byte[8192];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var line = new StringBuilder();
            try
            {
                int n;
                while ((n = fs.Read(bytes, 0, bytes.Length)) > 0)
                {
                    var c = decoder.GetChars(bytes, 0, n, chars, 0);
                    for (var i = 0; i < c; i++)
                    {
                        var ch = chars[i];
                        if (ch == '\n')
                        {
                            Emit(line, stream, onLine);
                        }
                        else if (ch == '\r')
                        {
                            // "\r\n" ends a line; a lone "\r" (progress bars) restarts it.
                            if (i + 1 < c && chars[i + 1] == '\n') continue;
                            if (i + 1 < c) line.Clear();
                        }
                        else line.Append(ch);
                        if (line.Length > 16384) Emit(line, stream, onLine);
                    }
                }
            }
            catch (IOException) { /* pipe broken: process gone */ }
            if (line.Length > 0) Emit(line, stream, onLine);
        })
        { IsBackground = true, Name = "rm-pipe-" + stream };
        thread.Start();
    }

    private static void Emit(StringBuilder line, LogStream stream, Action<LogStream, string> onLine)
    {
        var text = line.ToString();
        line.Clear();
        try { onLine(stream, text); } catch { /* logging must never kill the reader */ }
    }

    public static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(k) || k.Contains('=') && !k.StartsWith('=')) continue;
            sb.Append(k).Append('=').Append(v).Append('\0');
        }
        sb.Append('\0');
        return sb.ToString();
    }

    /// <summary>Quotes an argument for a cmd.exe command line. Quoting also disables & | &lt; &gt; ^ ( ).</summary>
    public static string QuoteArg(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        if (arg.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', ',', ';', '=']) < 0) return arg;
        return "\"" + arg.Replace("\"", "\"\"") + "\"";
    }

    public static string BuildCommandLine(string command, IEnumerable<string> args)
    {
        var sb = new StringBuilder(command.Contains(' ') && !command.StartsWith('"') && File.Exists(command) ? "\"" + command + "\"" : command);
        foreach (var a in args) sb.Append(' ').Append(QuoteArg(a));
        return sb.ToString();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _wait.Unregister(null);
        _job.Dispose(); // KILL_ON_JOB_CLOSE: anything still alive dies here.
        Native.CloseHandle(_stdinWrite);
        _waitHandle.Dispose();
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, true);
    }
}
