namespace Infra;

using System;

public sealed class ProcessRunnerOptions
{
    /// <summary>Tempo de graça após TERM antes de aplicar KILL (por default 500 ms).</summary>
    public TimeSpan GracefulWaitBeforeKill { get; init; } = TimeSpan.FromMilliseconds(500);
}