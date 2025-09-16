using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace Infra;

public class ProcessRunnerFactoryTests : IDisposable
{
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private IProcessRunnerKiller? _runner;

    public void Dispose()
    {
        _runner?.Dispose();
        _runner = null;
    }

    [Fact(DisplayName = "Factory cria UnixProcessRunnerKiller em Linux/macOS")]
    public void Create_ShouldReturnUnixRunner_OnUnix()
    {
        if (OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Teste aplicável apenas a Linux/macOS.");

        var options = new ProcessRunnerOptions { GracefulWaitBeforeKill = TimeSpan.FromMilliseconds(123) };

        _runner = ProcessRunnerFactory.Create(options, _loggerFactory);

        Assert.NotNull(_runner);
        Assert.IsType<UnixProcessRunnerKiller>(_runner);
    }

    [Fact(DisplayName = "Factory cria WindowsProcessRunnerKiller no Windows")]
    public void Create_ShouldReturnWindowsRunner_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Teste aplicável apenas a Windows.");

        var options = new ProcessRunnerOptions { GracefulWaitBeforeKill = TimeSpan.FromMilliseconds(234) };

        _runner = ProcessRunnerFactory.Create(options, _loggerFactory);

        Assert.NotNull(_runner);
        Assert.IsType<WindowsProcessRunnerKiller>(_runner);
    }

    [Fact(DisplayName = "Factory aceita options nulo e não lança")]
    public void Create_ShouldWork_WithNullOptions()
    {
        // roda em qualquer SO
        _runner = ProcessRunnerFactory.Create(options: null, loggerFactory: _loggerFactory);

        Assert.NotNull(_runner);
        // apenas garante que veio alguma implementação válida
        Assert.True(
            _runner is UnixProcessRunnerKiller || _runner is WindowsProcessRunnerKiller,
            $"Tipo inesperado: {_runner.GetType().FullName}"
        );
    }
}
