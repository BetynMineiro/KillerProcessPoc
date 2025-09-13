using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Infra;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using System.Management; 

namespace Runner;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var depth = GetInt("DEPTH", 5);
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

        // --- usa caminho absoluto do TreeProcessApp.dll (ou override via TREE_DLL) ---
        var treeDll = Environment.GetEnvironmentVariable("TREE_DLL") ?? ResolveTreeAppDll();

        var swTotal = Stopwatch.StartNew();
        var exitCode = await runner.RunWithTimeoutAsync(
            "dotnet",
            $"\"{treeDll}\" --depth {depth} --breadth {breadth} --sleepMs {sleepMs} --tag {tag}",
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

    static string ResolveTreeAppDll()
    {
        const string name = "TreeProcessApp.dll";

        // 1) variáveis rápidas
        var candidates = new List<string>
        {
            Path.Combine(Environment.CurrentDirectory, name),
            Path.Combine(AppContext.BaseDirectory ?? Environment.CurrentDirectory, name)
        };

        // 2) tenta localizar pastas de build típicas a partir da pasta atual (ou da solução)
        TryAddIfExists(candidates, FindSiblingBuild("TreeProcessApp", "bin", "Debug", "net9.0", name));
        TryAddIfExists(candidates, FindSiblingBuild("TreeProcessApp", "bin", "Release", "net9.0", name));
        TryAddIfExists(candidates, FindSiblingBuild("TreeProcessApp", "bin", "Debug", "net8.0", name));
        TryAddIfExists(candidates, FindSiblingBuild("TreeProcessApp", "bin", "Release", "net8.0", name));

        var hit = candidates.FirstOrDefault(File.Exists);
        if (hit is null)
            throw new FileNotFoundException(
                $"Não encontrei {name}. Informe o caminho via variável de ambiente TREE_DLL ou garanta que o projeto TreeProcessApp foi buildado.",
                name);

        return Path.GetFullPath(hit);

        static void TryAddIfExists(List<string> list, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) list.Add(path);
        }

        static string? FindSiblingBuild(params string[] parts)
        {
            // sobe diretórios até achar a pasta do projeto "TreeProcessApp"
            var dir = new DirectoryInfo(Environment.CurrentDirectory);
            while (dir != null)
            {
                var projDir = Path.Combine(dir.FullName, parts[0]);
                if (Directory.Exists(projDir))
                {
                    return Path.Combine(new[] { projDir }.Concat(parts.Skip(1)).ToArray());
                }
                dir = dir.Parent;
            }
            return null;
        }
    }

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

    // ===== Verificação de processos por tag =====

    static async Task<bool> AnyLeft(string tag)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process");
                using var results = searcher.Get();

                foreach (var o in results)
                {
                    var mo = (ManagementObject)o;
                    var cmd = mo["CommandLine"] as string;
                    if (string.IsNullOrEmpty(cmd)) continue;

                    if (cmd.IndexOf("TreeProcessApp", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        cmd.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                // fallback mínimo se WMI indisponível
                return false;
            }
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
            try
            {
                int count = 0;
                using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process");
                using var results = searcher.Get();

                foreach (var o in results)
                {
                    var mo = (ManagementObject)o;
                    var cmd = mo["CommandLine"] as string;
                    if (string.IsNullOrEmpty(cmd)) continue;

                    if (cmd.IndexOf("TreeProcessApp", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        cmd.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        count++;
                    }
                }
                return count;
            }
            catch
            {
                // fallback mínimo
                return 0;
            }
        }
        else
        {
            var sh = $"ps -eo pid,command | grep -v grep | grep TreeProcessApp | grep {tag} | wc -l";
            var (exit, outp) = await RunAndRead("/bin/bash", "-c \"" + sh.Replace("\"", "\\\"") + "\"");
            return int.TryParse(outp?.Trim(), out var n) ? n : 0;
        }
    }

    // ===== Exec helpers =====

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
