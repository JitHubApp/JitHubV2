using MarkdownRenderer.Svg.Resvg.Internal;
using MarkdownRenderer.Svg.Resvg.Internal.Fonts;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Transport;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Forwards a worker's font-catalog host services but holds every exchange
/// until <paramref name="release"/> completes, ignoring cancellation, so the
/// supplier's preparation stays in flight across worker retirement.
/// </summary>
internal sealed class BlockingFontCatalogExchangeHost(
    IWorkerFontCatalogHost inner,
    TaskCompletionSource entered,
    Task release) : IWorkerFontCatalogHost
{
    public SafeProcessHandle Process => inner.Process;

    public WorkerObjectSecurity.WorkerObjectSecurityContext ObjectSecurity => inner.ObjectSecurity;

    public void ThrowIfDisposed() => inner.ThrowIfDisposed();

    public async ValueTask<WorkerResponse> ExchangeAsync(
        WorkerRequest request,
        TimeSpan deadline,
        CancellationToken cancellationToken,
        SharedMemoryLease? sharedMemory)
    {
        entered.TrySetResult();
        await release.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await inner.ExchangeAsync(request, deadline, cancellationToken, sharedMemory).ConfigureAwait(false);
    }

    public void DisposeForRestart() => inner.DisposeForRestart();
}
