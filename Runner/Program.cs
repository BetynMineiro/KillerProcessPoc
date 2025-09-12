using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Infra;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Runner;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var depth = GetInt("DEPTH", 3);
        var breadth = GetInt("BREADTH", 5);
        var sleepMs = GetInt("SLEEPMs", 300000);
        var timeoutMs = GetInt("TIMEOUTMs", 5000);
        var verifyDelayMs = GetInt("VERIFY_DELAYMs", 1200);
        var tag = Environment.GetEnvironmentVariable("TAG") ?? ("TEST_" + Guid.NewGuid().ToString("N")[..8]);

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddConsole(o => o.FormatterName = ConsoleFormatterNames.Simple);
            builder.AddSimpleConsole(o =>
            {
                o.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                o.UseUtcTimestamp = false;
                o.IncludeScopes = false;
            });
        });
        var logger = loggerFactory.CreateLogger("Runner");

        logger.LogInformation("Starting on {OS} depth={Depth} breadth={Breadth} timeoutMs={Timeout} tag={Tag}",
            RuntimeInformation.OSDescription, depth, breadth, timeoutMs, tag);

        var options = new ProcessRunnerOptions { GracefulWaitBeforeKill = TimeSpan.FromMilliseconds(500) };
        var runner = ProcessRunnerFactory.Create(options, loggerFactory); // escolhe Unix/Windows automaticamente

        var swTotal = Stopwatch.StartNew();
        var exitCode = await runner.RunWithTimeoutAsync(
            "dotnet",
            $"TreeProcessApp.dll --depth {depth} --breadth {breadth} --sleepMs {sleepMs} --tag {tag}",
            TimeSpan.FromMilliseconds(timeoutMs)
        );
        swTotal.Stop();

        await Task.Delay(verifyDelayMs); // tempo pro SO reaportar estado

        var countBefore = await CountByTag(tag);
        var anyLeft = await AnyLeft(tag);
        var countAfter = await CountByTag(tag);

        // métricas por nível (modelo de árvore)
        var openedByLevel = ComputeOpenedByLevel(depth, breadth);
        var openedTotal = openedByLevel.Values.Sum();

        Dictionary<int, long>? closedByLevel = null;
        long closedTotal;
        if (!anyLeft)
        {
            closedByLevel = new Dictionary<int, long>(openedByLevel);
            closedTotal = openedTotal;
        }
        else
        {
            closedTotal = Math.Max(0, openedTotal - Math.Max(countAfter, 0));
        }

        var metrics = new
        {
            started_at = DateTimeOffset.UtcNow,
            os = RuntimeInformation.OSDescription,
            depth,
            breadth,
            timeout_ms = timeoutMs,
            graceful_ms = (int)options.GracefulWaitBeforeKill.TotalMilliseconds,
            tag,
            runner_exit_code = exitCode,
            total_elapsed_ms = swTotal.ElapsedMilliseconds,
            processes_seen_before_verify = countBefore,
            processes_seen_after_verify = countAfter,
            killed_tree_confirmed = !anyLeft,
            opened_total = openedTotal,
            opened_by_level = openedByLevel,
            closed_total = closedTotal,
            closed_by_level = closedByLevel
        };

        Console.WriteLine("=== METRICS ===");
        Console.WriteLine(JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));

        if (anyLeft)
        {
            logger.LogError("Verification failed: leftover processes with tag {Tag}", tag);
            return 2;
        }

        logger.LogInformation("Verification passed: no leftover processes for tag {Tag}", tag);
        return 0;
    }

    // ===== Helpers =====

    static Dictionary<int, long> ComputeOpenedByLevel(int depth, int breadth)
    {
        var dict = new Dictionary<int, long>();
        for (int level = 0; level <= depth; level++)
            dict[level] = Pow(breadth, level);
        return dict;
    }

    static long Pow(int b, int exp)
    {
        long r = 1;
        for (int i = 0; i < exp; i++) r *= b;
        return r;
    }

    static int GetInt(string env, int def) => int.TryParse(Environment.GetEnvironmentVariable(env), out var v) ? v : def;

    static async Task<bool> AnyLeft(string tag)
    {
        if (OperatingSystem.IsWindows())
        {
            // Retorna "1" se houver ao menos 1 processo com a tag; senão "0".
            var ps = $@"
$tag = '{tag}';
$found = Get-CimInstance Win32_Process |
    Where-Object {{
        $_.CommandLine -ne $null -and
        $_.CommandLine -like '*TreeProcessApp*' -and
        $_.CommandLine -like ('*' + $tag + '*')
    }} |
    Select-Object -First 1;
if ($found) {{ '1' }} else {{ '0' }}
";
            var (exit, outp) = await RunAndRead("powershell", $"-NoProfile -Command \"{ps}\"");
            var trimmed = outp?.Trim();
            return trimmed == "1";
        }
        else
        {
            var sh = $"ps -eo pid,command | grep -v grep | grep TreeProcessApp | grep {tag} | head -n 1";
            return await RunAndCheck("/bin/bash", "-c \"" + sh.Replace("\"", "\\\"") + "\"");
        }
    }

    static async Task<int> CountByTag(string tag)
    {
        if (OperatingSystem.IsWindows())
        {
            // Retorna somente um inteiro puro
            var ps = $@"
$tag = '{tag}';
(Get-CimInstance Win32_Process |
    Where-Object {{
        $_.CommandLine -ne $null -and
        $_.CommandLine -like '*TreeProcessApp*' -and
        $_.CommandLine -like ('*' + $tag + '*')
    }} |
    Measure-Object).Count
";
            var (exit, outp) = await RunAndRead("powershell", $"-NoProfile -Command \"{ps}\"");
            return int.TryParse(outp?.Trim(), out var n) ? n : 0;
        }
        else
        {
            var sh = $"ps -eo pid,command | grep -v grep | grep TreeProcessApp | grep {tag} | wc -l";
            var (exit, outp) = await RunAndRead("/bin/bash", "-c \"" + sh.Replace("\"", "\\\"") + "\"");
            return int.TryParse(outp?.Trim(), out var n) ? n : 0;
        }
    }

    static async Task<bool> RunAndCheck(string file, string args)
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
        if (p == null) return false;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return !string.IsNullOrWhiteSpace(output);
    }

    static async Task<(int exitCode, string? stdout)> RunAndRead(string file, string args)
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
        if (p == null) return (-1, null);
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, output);
    }
}
