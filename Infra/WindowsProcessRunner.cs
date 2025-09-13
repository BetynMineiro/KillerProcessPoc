namespace Infra;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public sealed class WindowsProcessRunner : IProcessRunner
{
    private readonly TimeSpan _gracefulWaitBeforeKill;
    private readonly ILogger? _logger;
    private Process? _proc;
    private JobHandle? _job; // wrapper com Dispose e Terminate

    public WindowsProcessRunner(ProcessRunnerOptions? options = null, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WindowsProcessRunner só suporta Windows.");

        _gracefulWaitBeforeKill = (options?.GracefulWaitBeforeKill).GetValueOrDefault(TimeSpan.FromMilliseconds(800));
        _logger = logger;
    }

    public Process Start(string fileName, string? arguments = null, string? workingDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? "",
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? Environment.CurrentDirectory : workingDir!,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start process.");
        _logger?.LogInformation("Started Windows process PID={Pid} Path={Path} Args={Args}", _proc.Id, fileName, arguments);

        try
        {
            _job = JobHandle.Create(killOnClose: true);
            _job.Assign(_proc);
            _logger?.LogInformation("Assigned PID={Pid} to Windows Job (KillOnClose).", _proc.Id);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create/assign Windows Job. Will fallback to taskkill.");
            _job = null;
        }

        return _proc;
    }

    public async Task<int> RunWithTimeoutAsync(string fileName, string? arguments, TimeSpan timeout, System.Threading.CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        Start(fileName, arguments);
        if (_proc is null) throw new InvalidOperationException("Process was not started.");

        using var ctsLinked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        var exitedTask = _proc.WaitForExitAsync(ctsLinked.Token);

        if (await Task.WhenAny(exitedTask, Task.Delay(timeout, ctsLinked.Token)) == exitedTask)
        {
            sw.Stop();
            _logger?.LogInformation("PID={Pid} naturally exited in {Ms} ms code={Code}", _proc.Id, sw.ElapsedMilliseconds, _proc.ExitCode);
            return _proc.ExitCode;
        }

        _logger?.LogWarning("Timeout {Ms} ms for PID={Pid}. Discovering descendants and sending close (Job/taskkill)...", timeout.TotalMilliseconds, _proc.Id);
        await LogDescendantsSnapshot("Before TERM");
        await KillTreeAsync(force: false);

        // Grace pequena
        var graceSw = Stopwatch.StartNew();
        var exited = await _proc.WaitForExitAsync(ctsLinked.Token).WaitAsync(_gracefulWaitBeforeKill, ctsLinked.Token)
                     .ContinueWith(t => t.Status == TaskStatus.RanToCompletion, ctsLinked.Token);

        if (!exited)
        {
            _logger?.LogWarning("PID={Pid} still alive after grace ({Ms} ms). FORCING kill tree...", _proc.Id, _gracefulWaitBeforeKill.TotalMilliseconds);
            await LogDescendantsSnapshot("Before KILL");
            await KillTreeAsync(force: true);
            await _proc.WaitForExitAsync(ctsLinked.Token);
        }
        graceSw.Stop();

        sw.Stop();
        _logger?.LogInformation("PID={Pid} terminated. total_elapsed_ms={Total} grace_wait_ms={Grace} exit_code={Code}",
            _proc.Id, sw.ElapsedMilliseconds, graceSw.ElapsedMilliseconds, _proc.ExitCode);

        return _proc.ExitCode;
    }

    public async Task KillTreeAsync(bool force = false)
    {
        if (_proc is null) return;

        try
        {
            if (_job is not null)
            {
                if (force)
                {
                    _logger?.LogInformation("Windows: TerminateJobObject for PID={Pid}", _proc.Id);
                    _job.Terminate(exitCode: 1); // mata TODOS do Job imediatamente
                }
                else
                {
                    _logger?.LogInformation("Windows: disposing Job to kill entire tree for PID={Pid}", _proc.Id);
                    _job.Dispose(); // KILL_ON_JOB_CLOSE
                }
                _job = null;
            }
            else
            {
                _logger?.LogWarning("Windows Job not present. Using taskkill fallback. PID={Pid} Force={Force}", _proc.Id, force);
                await RunAndWait("taskkill", $"/PID {_proc.Id} /T {(force ? "/F" : "")}");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "KillTreeAsync: error killing tree for PID={Pid}", _proc.Id);
        }
    }

    public void Dispose()
    {
        // 1) fecha/termina Job primeiro
        try
        {
            if (_job is not null)
            {
                _logger?.LogDebug("Dispose(): disposing Windows Job (KillOnClose).");
                _job.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Dispose(): error disposing Windows Job.");
        }
        finally
        {
            _job = null;
        }

        // 2) Se o processo ainda estiver vivo, tenta terminar
        if (_proc is { HasExited: false })
        {
            var pid = _proc.Id;
            for (var attempt = 1; attempt <= 3 && _proc is { HasExited: false }; attempt++)
            {
                try
                {
                    _logger?.LogWarning("Dispose(): attempt {Attempt} graceful Kill() PID={Pid}", attempt, pid);
                    _proc.Kill(entireProcessTree: false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Dispose(): Kill() failed (attempt {Attempt}) PID={Pid}", attempt, pid);
                }

                if (_proc.WaitForExit(200)) break;

                try
                {
                    if (_job is not null)
                    {
                        _logger?.LogWarning("Dispose(): attempt {Attempt} TerminateJobObject PID={Pid}", attempt, pid);
                        _job.Terminate(1);
                    }
                    else
                    {
                        _logger?.LogWarning("Dispose(): attempt {Attempt} FORCE taskkill /T /F PID={Pid}", attempt, pid);
                        RunSync("taskkill", $"/PID {pid} /T /F");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Dispose(): FORCE termination failed (attempt {Attempt}) PID={Pid}", attempt, pid);
                }

                _proc.WaitForExit(200);
            }

            if (_proc is { HasExited: false })
                _logger?.LogError("Dispose(): PID={Pid} still alive after retries.", pid);
            else
                _logger?.LogInformation("Dispose(): PID={Pid} exited.", pid);
        }

        try { _proc?.Dispose(); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Dispose(): failed to dispose Process PID={Pid}", _proc?.Id); }
        finally { _proc = null; }
    }

    // ---------- Helpers: discovery & shell ----------
    private async Task LogDescendantsSnapshot(string stage)
    {
        if (_proc is null) return;
        try
        {
            var set = await GetDescendantsAsync(_proc.Id);
            var sb = new StringBuilder();
            sb.Append($"[{stage}] PID={_proc.Id} descendants_count={set.Count}");
            if (set.Count > 0) sb.Append(" list=[" + string.Join(",", set) + "]");
            _logger?.LogInformation(sb.ToString());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Desc discovery failed for PID={Pid}", _proc.Id);
        }
    }

    // ======= Toolhelp32Snapshot para montar árvore por PPID (confiável) =======
    private static Task<HashSet<int>> GetDescendantsAsync(int rootPid)
    {
        var set = GetDescendantsViaToolhelp(rootPid);
        return Task.FromResult(set);

        static HashSet<int> GetDescendantsViaToolhelp(int root)
        {
            var result = new HashSet<int>();
            var map = new Dictionary<int, List<int>>();

            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == INVALID_HANDLE_VALUE) return result;

            try
            {
                var pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();

                if (Process32First(snap, ref pe))
                {
                    do
                    {
                        int pid = (int)pe.th32ProcessID;
                        int ppid = (int)pe.th32ParentProcessID;

                        if (!map.TryGetValue(ppid, out var list))
                            map[ppid] = list = new List<int>();
                        list.Add(pid);
                    }
                    while (Process32Next(snap, ref pe));
                }
            }
            finally
            {
                CloseHandle(snap);
            }

            // DFS a partir do root
            var stack = new Stack<int>();
            var visited = new HashSet<int> { root };
            stack.Push(root);

            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!map.TryGetValue(cur, out var children)) continue;

                foreach (var c in children)
                {
                    if (visited.Add(c))
                    {
                        result.Add(c);
                        stack.Push(c);
                    }
                }
            }

            return result;
        }
    }

    private static async Task RunAndWait(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        });
        if (p != null) await p.WaitForExitAsync();
    }

    private static async Task<(int exitCode, string? stdout)> RunAndRead(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        });
        if (p == null) return (-1, null);
        var o = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, o);
    }

    private static void RunSync(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        p?.WaitForExit();
    }

    // ================== Job wrapper (com TerminateJobObject) ==================
    private sealed class JobHandle : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;

        private JobHandle(IntPtr h) { _handle = h; }

        public static JobHandle Create(bool killOnClose)
        {
            var h = CreateJobObject(IntPtr.Zero, null);
            if (h == IntPtr.Zero) throw new InvalidOperationException("CreateJobObject failed.");

            if (killOnClose)
            {
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOBOBJECT_LIMIT_FLAGS.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

                if (!SetInformationJobObject(h, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                        ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    CloseHandle(h);
                    throw new InvalidOperationException("SetInformationJobObject failed.");
                }
            }

            return new JobHandle(h);
        }

        public void Assign(Process p)
        {
            if (!AssignProcessToJobObject(_handle, p.Handle))
                throw new InvalidOperationException("AssignProcessToJobObject failed.");
        }

        public void Terminate(uint exitCode)
        {
            if (_handle == IntPtr.Zero) return;
            if (!TerminateJobObject(_handle, exitCode))
                throw new InvalidOperationException("TerminateJobObject failed.");
            Close();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
        }

        private void Close()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

        // P/Invoke
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, JOBOBJECTINFOCLASS infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, int cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hHandle);

        private enum JOBOBJECTINFOCLASS
        {
            JobObjectExtendedLimitInformation = 9
        }

        [Flags]
        private enum JOBOBJECT_LIMIT_FLAGS : uint
        {
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public JOBOBJECT_LIMIT_FLAGS LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public long Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
    }

    // ============ P/Invoke Toolhelp32Snapshot ============
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
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
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
