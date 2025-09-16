namespace Infra;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public sealed class LinuxProcessRunnerKillerSystemdScope : IProcessRunnerKiller, IDisposable
{
    private readonly ILogger? _logger;
    private readonly TimeSpan _graceWait;
    private Process? _proc;        // processo do systemd-run (fica em foreground enquanto o alvo roda)
    private string? _unitName;     // nome da scope criada

    public LinuxProcessRunnerKillerSystemdScope(TimeSpan? graceWait = null, ILogger? logger = null)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("LinuxProcessRunnerKillerSystemdScope suporta apenas Linux.");
        _graceWait = graceWait ?? TimeSpan.FromMilliseconds(500);
        _logger = logger;

        // Pré-checagens simples
        if (!ExistsOnPath("systemd-run") || !ExistsOnPath("systemctl"))
            throw new PlatformNotSupportedException("Requer systemd: 'systemd-run' e 'systemctl' devem estar disponíveis no PATH.");
    }

    public Process Start(string fileName, string? arguments = null, string? workingDir = null)
    {
        var exec = ResolveExecutablePath(fileName);
        _unitName = $"runner-{Environment.UserName}-{Environment.ProcessId}-{Guid.NewGuid():N}.scope";

        var psi = new ProcessStartInfo
        {
            FileName = ResolveExecutablePath("systemd-run"),
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? Environment.CurrentDirectory : workingDir!,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        // systemd-run --user --scope --unit <unit> --property=KillMode=mixed --collect --same-dir <exec> <args...>
        psi.ArgumentList.Add("--user");
        psi.ArgumentList.Add("--scope");
        psi.ArgumentList.Add("--unit"); psi.ArgumentList.Add(_unitName);
        psi.ArgumentList.Add("--property=KillMode=mixed");
        psi.ArgumentList.Add("--collect");
        psi.ArgumentList.Add("--same-dir");
        psi.ArgumentList.Add(exec);
        foreach (var a in SplitArgs(arguments)) psi.ArgumentList.Add(a);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Falha ao iniciar systemd-run.");
        _logger?.LogInformation("systemd scope: unit={Unit} exec={Exec} args={Args}", _unitName, exec, arguments);
        return _proc;
    }

    public async Task<int> RunWithTimeoutAsync(string fileName, string? arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        Start(fileName, arguments);
        if (_proc is null) throw new InvalidOperationException("Processo não iniciado.");

        var exitedTask = _proc.WaitForExitAsync(ct);
        if (await Task.WhenAny(exitedTask, Task.Delay(timeout, ct)) == exitedTask)
            return _proc.ExitCode;

        _logger?.LogWarning("Timeout. Killing unit={Unit}", _unitName);
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
        if (_proc is null || string.IsNullOrWhiteSpace(_unitName)) return;
        var sig = force ? "KILL" : "TERM";
        await RunAndWait("systemctl", $"--user kill --signal={sig} {_unitName}");
    }

    public void Dispose()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                try { KillTreeAsync(force: true).GetAwaiter().GetResult(); } catch { /* ignore */ }
                _proc.WaitForExit(500);
            }
        }
        catch { /* ignore */ }
        finally
        {
            try { _proc?.Dispose(); } catch { /* ignore */ }
            _proc = null;
        }
    }

    // ------------- helpers -------------
    private static string[] SplitArgs(string? args)
        => string.IsNullOrWhiteSpace(args) ? Array.Empty<string>() : args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool ExistsOnPath(string cmd)
        => !string.IsNullOrEmpty(ResolveExecutablePath(cmd));

    private static string ResolveExecutablePath(string fileName)
    {
        if (Path.IsPathRooted(fileName) || fileName.Contains('/')) return fileName;

        // which
        var whichPath = File.Exists("/usr/bin/which") ? "/usr/bin/which" :
                        File.Exists("/bin/which") ? "/bin/which" : null;
        if (whichPath is not null)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = whichPath,
                    Arguments = fileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                var s = p?.StandardOutput.ReadToEnd();
                p?.WaitForExit(800);
                var found = s?.Trim();
                if (!string.IsNullOrWhiteSpace(found) && File.Exists(found))
                    return found!;
            }
            catch { /* ignore */ }
        }

        // PATH manual
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var cand = Path.Combine(dir, fileName);
                if (File.Exists(cand)) return cand;
            }
        }

        return fileName; // deixa falhar mais à frente (bom p/ log/diagnóstico)
    }

    private static async Task RunAndWait(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = ResolveExecutablePath(file),
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        });
        if (p != null) await p.WaitForExitAsync();
    }
}
