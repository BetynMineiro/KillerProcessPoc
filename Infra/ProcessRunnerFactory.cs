namespace Infra;

using System;
using Microsoft.Extensions.Logging;

public static class ProcessRunnerFactory
{
    public static IProcessRunner Create(ProcessRunnerOptions? options, ILoggerFactory loggerFactory)
    {
        
        if (OperatingSystem.IsWindows())
            return new WindowsProcessRunner(options, loggerFactory.CreateLogger<WindowsProcessRunner>());

        return new UnixProcessRunner(options, loggerFactory.CreateLogger<UnixProcessRunner>());
    }
}