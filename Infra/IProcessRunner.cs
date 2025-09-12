namespace Infra;

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

public interface IProcessRunner : IDisposable
{
    Process Start(string fileName, string? arguments = null, string? workingDir = null);
    Task<int> RunWithTimeoutAsync(string fileName, string? arguments, TimeSpan timeout, CancellationToken ct = default);
    Task KillTreeAsync(bool force = false);
}