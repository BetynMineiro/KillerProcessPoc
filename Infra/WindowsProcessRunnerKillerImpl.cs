namespace Infra;

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public sealed class WindowsProcessRunnerKillerImpl : IProcessRunnerKiller
{
    private readonly ILogger? _logger;
    private readonly TimeSpan _graceWait;
    private Process? _proc;
    private JobHandle? _job;

    public WindowsProcessRunnerKillerImpl(TimeSpan? graceWait = null, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WindowsProcessRunnerSimple suporta apenas Windows.");

        _logger = logger;
        _graceWait = graceWait ?? TimeSpan.FromMilliseconds(500);
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
        _logger?.LogInformation("Started PID={Pid} {Exe} {Args}", _proc.Id, fileName, arguments);

        try
        {
            _job = JobHandle.Create(killOnClose: true);
            _job.Assign(_proc);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "JobObject unavailable. Will fallback to taskkill.");
            _job = null;
        }

        return _proc;
    }

    public async Task<int> RunWithTimeoutAsync(string fileName, string? arguments, TimeSpan timeout, System.Threading.CancellationToken ct = default)
    {
        Start(fileName, arguments);
        if (_proc is null) throw new InvalidOperationException("Process not started");

        var exitedTask = _proc.WaitForExitAsync(ct);
        if (await Task.WhenAny(exitedTask, Task.Delay(timeout, ct)) == exitedTask)
            return _proc.ExitCode;

        _logger?.LogWarning("Timeout. Killing PID={Pid}", _proc.Id);

        await KillTreeAsync(force: false);
        if (!_proc.HasExited)
        {
            await Task.Delay(_graceWait, ct);
            if (!_proc.HasExited)
                await KillTreeAsync(force: true);
        }

        await _proc.WaitForExitAsync(ct);
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
                    _job.Terminate(1);        
                else
                    _job.Dispose();            
                _job = null;
                return;
            }

            var flags = force ? "/F" : "";
            await RunAndWait("taskkill", $"/PID {_proc.Id} /T {flags}");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "KillTreeAsync fallback failed for PID={Pid}", _proc.Id);
        }
    }

    public void Dispose()
    {
            _job?.Dispose();
            _job = null;


        if (_proc is { HasExited: false })
        {
            try { _proc.Kill(entireProcessTree: false); } catch { /* ignore */ }
            if (!_proc.WaitForExit(200))
            {
                try { RunSync("taskkill", $"/PID {_proc.Id} /T /F"); } catch { /* ignore */ }
                _proc.WaitForExit(200);
            }
        }

   _proc?.Dispose();
        _proc = null;
    }

    private static async Task RunAndWait(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (p != null) await p.WaitForExitAsync();
    }

    private static void RunSync(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        p?.WaitForExit();
    }

    private sealed class JobHandle : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;

        private JobHandle(IntPtr h) => _handle = h;

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
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

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
    }}
