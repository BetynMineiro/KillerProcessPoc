using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infra;

public class ProcessRunnerTests : IDisposable
{
    private readonly IProcessRunner _runner;

    public ProcessRunnerTests()
    {
        var loggerMock = new Mock<ILogger<IProcessRunner>>();
        var logger = loggerMock.Object;

        var options = new ProcessRunnerOptions
        {
            GracefulWaitBeforeKill = TimeSpan.FromMilliseconds(200)
        };

        _runner = new UnixProcessRunner(options, logger);
    }

    public void Dispose()
    {
        _runner.Dispose();
    }

    [Fact(DisplayName = "Start: deve lançar o processo")]
    public void Start_ShouldLaunchProcess()
    {
        // Arrange + Act
        var p = _runner.Start("sleep", "1");

        // Assert
        Assert.NotNull(p);
        Assert.True(p.Id > 0);
        Assert.False(p.HasExited);
    }

    [Fact(DisplayName = "RunWithTimeoutAsync: deve retornar exit code quando o processo sai naturalmente")]
    public async Task RunWithTimeoutAsync_ShouldReturnExitCode_WhenProcessExitsNaturally()
    {
        // Act
        // macOS/Linux costumam aceitar frações no sleep; se preferir, troque para "1" e timeout maior
        int exit = await _runner.RunWithTimeoutAsync("sleep", "0.2", TimeSpan.FromSeconds(2));

        // Assert
        Assert.Equal(0, exit);
    }

    [Fact(DisplayName = "RunWithTimeoutAsync: deve matar árvore ao estourar timeout")]
    public async Task RunWithTimeoutAsync_ShouldKillProcessTree_OnTimeout()
    {
        // Act
        int exit = await _runner.RunWithTimeoutAsync("sleep", "5", TimeSpan.FromMilliseconds(300));

        // Assert: em geral não é 0 (terminado por sinal)
        Assert.NotEqual(0, exit);
    }

    [Fact(DisplayName = "KillTreeAsync: deve encerrar processo em execução (force=true)")]
    public async Task KillTreeAsync_ShouldTerminateRunningProcess()
    {
        // Arrange
        var p = _runner.Start("sleep", "10");
        Assert.False(p.HasExited);

        // Act
        await _runner.KillTreeAsync(force: true);

        // Aguarda o processo morrer, com timeout de 2 segundos para evitar travamento
        var completed = await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(2000));
        Assert.True(completed == p.WaitForExitAsync(), 
            $"Processo PID={p.Id} não saiu após KillTreeAsync.");

        // Assert
        Assert.True(p.HasExited, $"Processo PID={p.Id} ainda está ativo após KillTreeAsync.");
    }

    [Fact(DisplayName = "Dispose: deve ser idempotente (não lançar em chamadas repetidas)")]
    public void Dispose_ShouldBeIdempotent()
    {
        // Act + Assert
        _runner.Dispose();
        _runner.Dispose(); // não deve lançar exceção
    }
}
