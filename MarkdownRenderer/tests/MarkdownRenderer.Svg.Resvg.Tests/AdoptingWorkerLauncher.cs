
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Hands the pool one already-connected test worker on its first start and
/// fails every later start, so a test can observe when the pool asks for a
/// replacement.
/// </summary>
internal sealed class AdoptingWorkerLauncher(WindowsWorkerProcess worker) : IWorkerProcessLauncher
{
    private int _startCount;

    internal int StartCount => Volatile.Read(ref _startCount);

    public Task<WindowsWorkerProcess> StartAsync(WorkerJob job, CancellationToken cancellationToken) =>
        Interlocked.Increment(ref _startCount) == 1
            ? Task.FromResult(worker)
            : Task.FromException<WindowsWorkerProcess>(
                new InvalidOperationException("The test launcher adopts only one worker."));
}
