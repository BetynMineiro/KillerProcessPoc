namespace Infra;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public sealed class UnixProcessRunnerKiller(ProcessRunnerOptions? options = null, ILogger? logger = null) : IProcessRunnerKiller
{
    private readonly TimeSpan _gracefulWaitBeforeKill = (options?.GracefulWaitBeforeKill).GetValueOrDefault(TimeSpan.FromMilliseconds(500));
    private Process? _proc;
    private bool _startedWithNewSessionOnUnix;

    private const int FallbackPasses = 5;
    private const int FallbackDelayMs = 150;

    public Process Start(string fileName, string? arguments = null, string? workingDir = null)
    {
        var isUnix = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst();
        if (!isUnix) throw new PlatformNotSupportedException("UnixProcessRunnerKiller só suporta Linux/macOS.");

        var workDir = string.IsNullOrWhiteSpace(workingDir) ? Environment.CurrentDirectory : workingDir!;
        var hasSetSid = FileExists("/usr/bin/setsid") || FileExists("/bin/setsid");
        var usePyShim = !hasSetSid && HasPython3();

        ProcessStartInfo psi;

        if (hasSetSid)
        {
            psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/setsid",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(fileName);
            foreach (var a in SplitArgsUnix(arguments)) psi.ArgumentList.Add(a);
            _startedWithNewSessionOnUnix = true;
            logger?.LogInformation("Unix: using setsid binary.");
        }
        else if (usePyShim)
        {
            const string script = "import os,sys; os.setsid(); os.execvp(sys.argv[1], sys.argv[1:])";
            psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/env",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("python3");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(fileName);
            foreach (var a in SplitArgsUnix(arguments)) psi.ArgumentList.Add(a);
            _startedWithNewSessionOnUnix = true;
            logger?.LogInformation("Unix: using python3 shim for setsid().");
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in SplitArgsUnix(arguments)) psi.ArgumentList.Add(a);
            _startedWithNewSessionOnUnix = false;
            logger?.LogWarning("Unix: no setsid and no python3; will rely on pgrep -P fallback on shutdown.");
        }

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start process.");
        logger?.LogInformation("Started Unix process PID={Pid} setsid={SetSid} Path={Path} Args={Args}",
            _proc.Id, _startedWithNewSessionOnUnix, fileName, arguments);

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
            logger?.LogInformation("PID={Pid} naturally exited in {Ms} ms code={Code}", _proc.Id, sw.ElapsedMilliseconds, _proc.ExitCode);
            return _proc.ExitCode;
        }

        logger?.LogWarning("Timeout {Ms} ms for PID={Pid}. Discovering descendants and sending TERM...", timeout.TotalMilliseconds, _proc.Id);
        await LogDescendantsSnapshot("Before TERM");
        await KillTreeAsync(force: false);

        // grace: se não houver descendentes, dá janela curta pro root
        var graceSw = Stopwatch.StartNew();
        var exited = await _proc.WaitForExitAsync(ctsLinked.Token).WaitAsync(_gracefulWaitBeforeKill, ctsLinked.Token)
            .ContinueWith(t => t.Status == TaskStatus.RanToCompletion, ctsLinked.Token);

        if (!exited)
        {
            var desc = await GetDescendantsAsync(_proc.Id);
            if (desc.Count == 0)
            {
                logger?.LogInformation("After TERM: no descendants left; giving root PID={Pid} a short window to exit...", _proc.Id);
                exited = _proc.WaitForExit(400);
            }

            if (!exited)
            {
                logger?.LogWarning("PID={Pid} still alive after grace ({Ms} ms). FORCING KILL on group/root...", _proc.Id, _gracefulWaitBeforeKill.TotalMilliseconds);
                await KillTreeAsync(force: true);
                await _proc.WaitForExitAsync(ctsLinked.Token);
            }
        }

        graceSw.Stop();

        sw.Stop();
        logger?.LogInformation("PID={Pid} terminated. total_elapsed_ms={Total} grace_wait_ms={Grace} exit_code={Code}",
            _proc.Id, sw.ElapsedMilliseconds, graceSw.ElapsedMilliseconds, _proc.ExitCode);

        return _proc.ExitCode;
    }

    public async Task KillTreeAsync(bool force = false)
    {
        if (_proc is null) return;

        try
        {
            if (_startedWithNewSessionOnUnix)
            {
                var sig = force ? "-KILL" : "-TERM";
                var target = "-" + _proc.Id; // PGID
                logger?.LogInformation("Unix: kill {Sig} {Target}", sig, target);
                await RunAndWait("/bin/kill", $"{sig} {target}");
            }
            else
            {
                logger?.LogWarning("Unix: no setsid; using recursive pgrep -P fallback. PID={Pid} Force={Force}", _proc.Id, force);
                await KillTreeUnixFallbackAsync(_proc.Id, force);
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "KillTreeAsync: failed to kill tree for PID={Pid}", _proc.Id);
        }
    }

    public void Dispose()
    {
        if (_proc is { HasExited: false })
        {
            var pid = _proc.Id;
            for (var attempt = 1; attempt <= 3 && _proc is { HasExited: false }; attempt++)
            {
                try
                {
                    logger?.LogWarning("Dispose(): attempt {Attempt} Kill() PID={Pid}", attempt, pid);
                    _proc.Kill(entireProcessTree: false);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Dispose(): Kill() failed (attempt {Attempt}) PID={Pid}", attempt, pid);
                }

                if (_proc.WaitForExit(200)) break;

                try
                {
                    logger?.LogWarning("Dispose(): attempt {Attempt} FORCE kill PID={Pid}", attempt, pid);
                    ForceKillOnce(_proc, _startedWithNewSessionOnUnix);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Dispose(): FORCE kill failed (attempt {Attempt}) PID={Pid}", attempt, pid);
                }

                _proc.WaitForExit(200);
            }

            if (_proc is { HasExited: false })
                logger?.LogError("Dispose(): PID={Pid} still alive after retries.", pid);
            else
                logger?.LogInformation("Dispose(): PID={Pid} exited.", pid);
        }

        try
        {
            _proc?.Dispose();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Dispose(): failed disposing Process PID={Pid}", _proc?.Id);
        }
        finally
        {
            _proc = null;
        }
    }

    // ---------- Unix helpers ----------
    private async Task KillTreeUnixFallbackAsync(int rootPid, bool force)
    {
        var sig = force ? "KILL" : "TERM";
        for (var i = 1; i <= FallbackPasses; i++)
        {
            var desc = await GetDescendantsAsync(rootPid);
            logger?.LogInformation("Unix fallback {Pass}/{Total}: root={Pid} descendants={Count} [{List}]",
                i, FallbackPasses, rootPid, desc.Count, string.Join(",", desc));

            var script = $@"
terminate_tree() {{
  local pid=""$1""
  for child in $(pgrep -P ""$pid""); do
    terminate_tree ""$child""
  done
  kill -s {sig} ""$pid"" 2>/dev/null || true
}}
terminate_tree {rootPid}
";
            await RunAndWait("/bin/bash", "-c '" + script.Replace("'", "'\"'\"'") + "'");

            if (!ProcessExists(rootPid))
            {
                logger?.LogInformation("Unix fallback: root PID={Pid} no longer exists after pass={Pass}.", rootPid, i);
                return;
            }

            await Task.Delay(FallbackDelayMs);
        }

        if (ProcessExists(rootPid))
        {
            logger?.LogWarning("Unix fallback: FORCE KILL root PID={Pid}.", rootPid);
            await RunAndWait("/bin/kill", $"-KILL {rootPid}");
        }
    }

    private static void ForceKillOnce(Process p, bool startedWithNewSessionOnUnix)
    {
        if (startedWithNewSessionOnUnix)
        {
            RunSync("/bin/kill", $"-KILL -{p.Id}"); // PGID
        }
        else
        {
            var script = $@"
pkill -KILL -P {p.Id} 2>/dev/null || true
kill -KILL {p.Id} 2>/dev/null || true
";
            RunSync("/bin/bash", "-c '" + script.Replace("'", "'\"'\"'") + "'");
        }
    }

    private async Task LogDescendantsSnapshot(string stage)
    {
        if (_proc is null) return;
        try
        {
            var set = await GetDescendantsAsync(_proc.Id);
            var sb = new StringBuilder();
            sb.Append($"[{stage}] PID={_proc.Id} descendants_count={set.Count}");
            if (set.Count > 0) sb.Append(" list=[" + string.Join(",", set) + "]");
            logger?.LogInformation(sb.ToString());
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Desc discovery failed for PID={Pid}", _proc.Id);
        }
    }

    private static async Task<HashSet<int>> GetDescendantsAsync(int rootPid)
    {
        var result = new HashSet<int>();
        var visited = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(rootPid);
        visited.Add(rootPid);

        while (stack.Count > 0)
        {
            var pid = stack.Pop();
            var (exit, output) = await RunAndRead("/bin/bash", "-c \"pgrep -P " + pid + " || true\"");
            if (exit != 0 || output is null) continue;
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(line.Trim(), out var child) || !visited.Add(child)) continue;
                result.Add(child);
                stack.Push(child);
            }
        }

        return result;
    }

    private static IEnumerable<string> SplitArgsUnix(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) yield break;
        var s = args!;
        var sb = new System.Text.StringBuilder();
        var inSingle = false;
        var inDouble = false;
        foreach (var c in s)
        {
            switch (c)
            {
                case '\'' when !inDouble:
                    inSingle = !inSingle;
                    continue;
                case '\"' when !inSingle:
                    inDouble = !inDouble;
                    continue;
            }

            if (char.IsWhiteSpace(c) && !inSingle && !inDouble)
            {
                if (sb.Length <= 0) continue;
                yield return sb.ToString();
                sb.Clear();
            }
            else sb.Append(c);
        }

        if (sb.Length > 0) yield return sb.ToString();
    }

    private static bool FileExists(string path)
    {
        try
        {
            return System.IO.File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool ProcessExists(int pid)
    {
        try
        {
            return !Process.GetProcessById(pid).HasExited;
        }
        catch
        {
            return false;
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

    private static bool HasPython3()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/env",
                Arguments = "python3 --version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            p?.WaitForExit(1500);
            return p is not null && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}