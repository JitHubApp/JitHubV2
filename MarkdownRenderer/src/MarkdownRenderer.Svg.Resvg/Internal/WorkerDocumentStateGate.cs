namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Serializes multi-frame operations that inspect or replace a worker's document
/// state. The gate intentionally has no Dispose path: outstanding waiters and
/// leases may safely finish while their owning worker is being retired.
/// </summary>
internal sealed class WorkerDocumentStateGate
{
    private readonly SemaphoreSlim _semaphore = new(initialCount: 1, maxCount: 1);

    public async ValueTask<Lease> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(this);
    }

    private void Exit() => _semaphore.Release();

    internal sealed class Lease(WorkerDocumentStateGate owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Exit();
        }
    }
}
