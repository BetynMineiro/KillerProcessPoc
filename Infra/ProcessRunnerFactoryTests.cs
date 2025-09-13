using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace Infra;

public class ProcessRunnerFactoryTests : IDisposable
{
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private IProcessRunner? _runner;

    public void Dispose()
    {
        _runner?.Dispose();
        _runner = null;
    }

    [Fact(DisplayName = "Factory cria UnixProcessRunner em Linux/macOS")]
    public void Create_ShouldReturnUnixRunner_OnUnix()
    {
        if (OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Teste aplicável apenas a Linux/macOS.");

        var options = new ProcessRunnerOptions { GracefulWaitBeforeKill = TimeSpan.FromMilliseconds(123) };

        _runner = ProcessRunnerFactory.Create(options, _loggerFactory);

        Assert.NotNull(_runner);
        Assert.IsType<UnixProcessRunner>(_runner);
    }

    [Fact(DisplayName = "Factory cria WindowsProcessRunner no Windows")]
    public void Create_ShouldReturnWindowsRunner_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Teste aplicável apenas a Windows.");

        var options = new ProcessRunnerOptions { GracefulWaitBeforeKill = TimeSpan.FromMilliseconds(234) };

        _runner = ProcessRunnerFactory.Create(options, _loggerFactory);

        Assert.NotNull(_runner);
        Assert.IsType<WindowsProcessRunner>(_runner);
    }

    [Fact(DisplayName = "Factory aceita options nulo e não lança")]
    public void Create_ShouldWork_WithNullOptions()
    {
        // roda em qualquer SO
        _runner = ProcessRunnerFactory.Create(options: null, loggerFactory: _loggerFactory);

        Assert.NotNull(_runner);
        // apenas garante que veio alguma implementação válida
        Assert.True(
            _runner is UnixProcessRunner || _runner is WindowsProcessRunner,
            $"Tipo inesperado: {_runner.GetType().FullName}"
        );
    }
}
