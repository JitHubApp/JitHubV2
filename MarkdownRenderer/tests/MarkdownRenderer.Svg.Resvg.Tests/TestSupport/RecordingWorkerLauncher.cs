using System.Collections.Concurrent;
using MarkdownRenderer.Svg.Resvg.Internal.Workers;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;

namespace MarkdownRenderer.Svg.Resvg.Tests.TestSupport;

/// <summary>
/// Starts real packaged workers exactly as production does and records each
/// one in start order, so a test can read a worker's published state.
/// </summary>
internal sealed class RecordingWorkerLauncher(ResvgMarkdownSvgRendererOptions options) : IWorkerProcessLauncher
{
    private readonly WindowsWorkerProcessLauncher _inner = new(WorkerPool.ResolveWorkerPath(options), options);
    private readonly ConcurrentQueue<WindowsWorkerProcess> _started = new();

    internal IReadOnlyCollection<WindowsWorkerProcess> Started => _started;

    public async Task<WindowsWorkerProcess> StartAsync(WorkerJob job, CancellationToken cancellationToken)
    {
        WindowsWorkerProcess worker = await _inner.StartAsync(job, cancellationToken).ConfigureAwait(false);
        _started.Enqueue(worker);
        return worker;
    }
}
