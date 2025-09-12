using System.Diagnostics;
using System.Reflection;

namespace TreeProcessApp;

internal static class Program
{
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.ProcessExit += (_, __) =>
        {
            Console.WriteLine($"[TreeProcessApp] PID={Environment.ProcessId} exiting");
            Console.Out.Flush();
        };

        int depth = 2, breadth = 2, sleepMs = 300_000;
        var tag = Guid.NewGuid().ToString("N");

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--depth" when i + 1 < args.Length:
                    depth = int.Parse(args[++i]);
                    break;
                case "--breadth" when i + 1 < args.Length:
                    breadth = int.Parse(args[++i]);
                    break;
                case "--sleepMs" when i + 1 < args.Length:
                    sleepMs = int.Parse(args[++i]);
                    break;
                case "--tag" when i + 1 < args.Length:
                    tag = args[++i];
                    break;
            }
        }

        Console.WriteLine($"[TreeProcessApp] PID={Environment.ProcessId} depth={depth} breadth={breadth} tag={tag}");
        Console.Out.Flush();

        if (depth > 0)
        {
            var exe = Assembly.GetEntryAssembly()!.Location;
            for (var i = 0; i < breadth; i++)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.GetExtension(exe).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : exe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                if (Path.GetExtension(exe).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    psi.ArgumentList.Add(exe);

                var children = new[]
                {
                    "--depth", (depth - 1).ToString(),
                    "--breadth", breadth.ToString(),
                    "--sleepMs", sleepMs.ToString(),
                    "--tag", tag
                };

                foreach (var arg in children)
                    psi.ArgumentList.Add(arg);

                Process.Start(psi);
            }
        }

        Thread.Sleep(sleepMs);
        return 0;
    }
}
