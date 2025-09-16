namespace Infra;

using System;
using Microsoft.Extensions.Logging;

public static class ProcessRunnerFactory
{
    public static IProcessRunnerKiller Create(ProcessRunnerOptions? options, ILoggerFactory loggerFactory)
    {
        
        if (OperatingSystem.IsWindows())
            return new WindowsProcessRunnerKillerImpl(TimeSpan.FromMilliseconds(500), loggerFactory.CreateLogger<WindowsProcessRunnerKiller>());

        return new LinuxProcessRunnerKillerSystemdScope(TimeSpan.FromMilliseconds(500), loggerFactory.CreateLogger<UnixProcessRunnerKiller>());
    }
}