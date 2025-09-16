namespace Infra;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public sealed class UnixProcessRunnerKillerCgroup : IProcessRunnerKiller, IDisposable
{
    private readonly TimeSpan _gracefulWaitBeforeKill;
    private readonly ILogger? _logger;

    private Process? _proc;            // processo do systemd-run (foreground)
    private string? _systemdUnit;      // nome da scope unit

    public UnixProcessRunnerKillerCgroup(ProcessRunnerOptions? options = null, ILogger? logger = null)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("UnixProcessRunnerKillerCgroup (systemd) só suporta Linux.");

        _gracefulWaitBeforeKill = (options?.GracefulWaitBeforeKill).GetValueOrDefault(TimeSpan.FromMilliseconds(500));
        _logger = logger;

        if (!HasSystemdRun() || !HasSystemctlUser())
            throw new PlatformNotSupportedException("Requer systemd: 'systemd-run' e 'systemctl --user' não disponíveis.");
    }

    public Process Start(string fileName, string? arguments = null, string? workingDir = null)
    {
        var workDir = string.IsNullOrWhiteSpace(workingDir) ? Environment.CurrentDirectory : workingDir!;
        var execPath = ResolveExecutablePath(fileName);

        // Nome único para a scope
        _systemdUnit = $"runner-{Environment.UserName}-{Environment.ProcessId}-{Guid.NewGuid():N}.scope";

        var psi = new ProcessStartInfo
        {
            FileName = ResolveExecutablePath("systemd-run"),
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        // systemd-run --user --scope --unit <unit> --property=KillMode=mixed --collect --same-dir <exec> <args...>
        psi.ArgumentList.Add("--user");
        psi.ArgumentList.Add("--scope");
        psi.ArgumentList.Add("--unit"); psi.ArgumentList.Add(_systemdUnit);
        psi.ArgumentList.Add("--property=KillMode=mixed");
        psi.ArgumentList.Add("--collect");
        psi.ArgumentList.Add("--same-dir");
        psi.ArgumentList.Add(execPath);
        foreach (var a in SplitArgsUnix(arguments)) psi.ArgumentList.Add(a);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start systemd-run.");
        _logger?.LogInformation("Unix(systemd): started under scope unit={Unit} Path={Path} Args={Args}", _systemdUnit, execPath, arguments);

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
            _logger?.LogInformation("PID(systemd-run)={Pid} naturally exited in {Ms} ms code={Code}", _proc.Id, sw.ElapsedMilliseconds, _proc.ExitCode);
            return _proc.ExitCode;
        }

        _logger?.LogWarning("Timeout {Ms} ms. Capturing PIDs from unit and sending TERM...", timeout.TotalMilliseconds);
        await LogUnitPidsSnapshot("Before TERM");
        await KillTreeAsync(force: false);

        // Grace curto
        var graceSw = Stopwatch.StartNew();
        var exited = await _proc.WaitForExitAsync(ctsLinked.Token).WaitAsync(_gracefulWaitBeforeKill, ctsLinked.Token)
                     .ContinueWith(t => t.Status == TaskStatus.RanToCompletion, ctsLinked.Token);

        if (!exited)
        {
            _logger?.LogWarning("Still alive after grace ({Ms} ms). Capturing PIDs and sending KILL...", _gracefulWaitBeforeKill.TotalMilliseconds);
            await LogUnitPidsSnapshot("Before KILL");
            await KillTreeAsync(force: true);
            await _proc.WaitForExitAsync(ctsLinked.Token);
        }

        graceSw.Stop();
        sw.Stop();

        _logger?.LogInformation("PID(systemd-run)={Pid} terminated. total_elapsed_ms={Total} grace_wait_ms={Grace} exit_code={Code}",
            _proc.Id, sw.ElapsedMilliseconds, graceSw.ElapsedMilliseconds, _proc.ExitCode);

        return _proc.ExitCode;
    }

    public async Task KillTreeAsync(bool force = false)
    {
        if (_proc is null || string.IsNullOrEmpty(_systemdUnit)) return;

        var sig = force ? "KILL" : "TERM";
        _logger?.LogInformation("systemd --user: kill unit={Unit} signal={Sig}", _systemdUnit, sig);
        await RunAndWait("systemctl", $"--user kill --signal={sig} {_systemdUnit}");
    }

    public void Dispose()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                try
                {
                    _logger?.LogWarning("Dispose(): sending KILL to unit={Unit}", _systemdUnit);
                    KillTreeAsync(force: true).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Dispose(): error sending KILL to unit={Unit}", _systemdUnit);
                }

                _proc.WaitForExit(500);
            }
        }
        finally
        {
            try { _proc?.Dispose(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Dispose(): failed to dispose systemd-run process PID={Pid}", _proc?.Id); }
            finally { _proc = null; }
        }
    }

    // ----- snapshots (PIDs da unit) -----
    private async Task LogUnitPidsSnapshot(string stage)
    {
        if (string.IsNullOrEmpty(_systemdUnit)) return;
        try
        {
            var pids = await GetUnitPids(_systemdUnit);
            var sb = new StringBuilder();
            sb.Append($"[{stage}] unit={_systemdUnit} pids_count={pids.Count}");
            if (pids.Count > 0) sb.Append(" list=[" + string.Join(",", pids) + "]");
            _logger?.LogInformation(sb.ToString());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error reading PIDs for unit={Unit}", _systemdUnit);
        }
    }

    private static async Task<HashSet<int>> GetUnitPids(string unit)
    {
        var set = new HashSet<int>();
        // `systemctl --user show <unit> -p PIDs` -> PIDs=123 456 ...
        var (exit, outp) = await RunAndRead("systemctl", $"--user show {unit} -p PIDs");
        if (exit != 0 || string.IsNullOrWhiteSpace(outp)) return set;

        foreach (var line in outp.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf("PIDs=", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var val = line[(idx + 5)..].Trim();
                foreach (var tok in val.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(tok, out var pid)) set.Add(pid);
            }
        }
        return set;
    }

    // ----- helpers -----
    private static IEnumerable<string> SplitArgsUnix(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) yield break;
        var s = args!;
        var sb = new System.Text.StringBuilder();
        bool inSingle = false, inDouble = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'' && !inDouble) { inSingle = !inSingle; continue; }
            if (c == '\"' && !inSingle) { inDouble = !inDouble; continue; }
            if (char.IsWhiteSpace(c) && !inSingle && !inDouble)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static bool HasSystemdRun()
        => FileExists("/usr/bin/systemd-run") || FileExists("/bin/systemd-run") || !string.IsNullOrEmpty(Which("systemd-run"));

    private static bool HasSystemctlUser()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = ResolveExecutablePath("systemctl"),
                Arguments = "--user is-active default.target",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            p?.WaitForExit(1000);
            return p is not null && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string ResolveExecutablePath(string fileName)
    {
        if (Path.IsPathRooted(fileName)) return fileName;

        var which = Which(fileName);
        if (!string.IsNullOrEmpty(which)) return which!;

        if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")
                             ?? Environment.GetEnvironmentVariable("DOTNET_ROOT_X64")
                             ?? Environment.GetEnvironmentVariable("DOTNET_ROOT_X86");
            if (!string.IsNullOrWhiteSpace(dotnetRoot))
            {
                var candidate = Path.Combine(dotnetRoot, "dotnet");
                if (File.Exists(candidate)) return candidate;
            }
            var common = new[]
            {
                "/usr/bin/dotnet",
                "/usr/local/bin/dotnet",
                "/usr/local/share/dotnet/dotnet",
                "/snap/bin/dotnet"
            };
            foreach (var p in common)
                if (File.Exists(p)) return p;
        }

        return fileName; // deixamos falhar e logar
    }

    private static string? Which(string cmd)
    {
        try
        {
            var whichPath = File.Exists("/usr/bin/which") ? "/usr/bin/which" :
                            File.Exists("/bin/which") ? "/bin/which" : null;
            if (whichPath is not null)
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = whichPath,
                    Arguments = cmd,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (p is not null)
                {
                    var s = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(1200);
                    var found = s?.Trim();
                    if (!string.IsNullOrWhiteSpace(found) && File.Exists(found))
                        return found!;
                }
            }

            var path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = Path.Combine(dir, cmd);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static bool FileExists(string path) { try { return File.Exists(path); } catch { return false; } }

    private static async Task RunAndWait(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = ResolveExecutablePath(file),
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
            FileName = ResolveExecutablePath(file),
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
}
