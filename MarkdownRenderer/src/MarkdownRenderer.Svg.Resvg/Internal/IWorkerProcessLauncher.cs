using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>Starts one isolated worker process inside the pool's job.</summary>
internal interface IWorkerProcessLauncher
{
    /// <summary>Starts a worker and completes its handshake.</summary>
    /// <param name="job">The pool's kill-on-close job.</param>
    /// <param name="cancellationToken">Cancels startup when the pool shuts down.</param>
    /// <returns>The started worker.</returns>
    Task<WindowsWorkerProcess> StartAsync(WorkerJob job, CancellationToken cancellationToken);
}
