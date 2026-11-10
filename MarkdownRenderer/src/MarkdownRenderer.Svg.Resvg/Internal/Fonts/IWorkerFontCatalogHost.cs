using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Transport;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal.Fonts;

/// <summary>
/// The worker process services that <see cref="WorkerFontCatalogSupplier"/>
/// borrows while it lends installed-font mappings to that worker.
/// </summary>
internal interface IWorkerFontCatalogHost
{
    /// <summary>The worker process that receives duplicated font handles.</summary>
    SafeProcessHandle Process { get; }

    /// <summary>The security context applied to handles lent to the worker.</summary>
    WorkerObjectSecurity.WorkerObjectSecurityContext ObjectSecurity { get; }

    /// <summary>Throws <see cref="ObjectDisposedException"/> once the worker is retired.</summary>
    void ThrowIfDisposed();

    /// <summary>Runs one request/response exchange on the worker transport.</summary>
    /// <param name="request">The request to send.</param>
    /// <param name="deadline">The exchange deadline.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <param name="sharedMemory">The mapping lent for this request, if any.</param>
    /// <returns>The worker's response.</returns>
    ValueTask<WorkerResponse> ExchangeAsync(
        WorkerRequest request,
        TimeSpan deadline,
        CancellationToken cancellationToken,
        SharedMemoryLease? sharedMemory);

    /// <summary>Retires the worker and waits for its process to exit.</summary>
    void DisposeForRestart();
}
