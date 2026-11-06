using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// One admitted use of a worker slot. Disposing it releases the slot back to
/// the pool exactly once.
/// </summary>
internal sealed class WorkerLease(
    WorkerScheduler owner,
    WorkerSlot slot,
    PendingWork pending) : IDisposable
{
    private int _disposed;

    public WorkerSlot Slot { get; } = slot;
    public PendingWork Pending { get; } = pending;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            owner.Release(Slot, Pending);
    }
}
