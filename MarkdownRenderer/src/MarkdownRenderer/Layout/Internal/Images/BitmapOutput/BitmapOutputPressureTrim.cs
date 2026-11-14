using System;
using System.Threading;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;

namespace MarkdownRenderer.Layout.Internal.Images.BitmapOutput;

/// <summary>
/// The best-effort, cache-only trim a <see cref="SharedCanvasBitmapCache"/>
/// runs when bitmap output admission rejects a demand. Requests coalesce into
/// one queued worker, and the clear never runs on the requesting thread, so
/// a rejection path never synchronously disposes Win2D resources (which may
/// run on the UI thread). The worker owns no ImageBox, lease, or bitmap
/// references.
/// </summary>
internal sealed class BitmapOutputPressureTrim
{
    private readonly Action _clearCache;
    private int _requested;
    private int _queued;

    internal BitmapOutputPressureTrim(Action clearCache)
    {
        ArgumentNullException.ThrowIfNull(clearCache);
        _clearCache = clearCache;
    }

    internal void Request()
    {
        Interlocked.Exchange(ref _requested, 1);
        Queue();
    }

    private void Queue()
    {
        if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0)
            return;

        try
        {
            // Reclamation has no document work or caller identity to inherit.
            // Do not retain ambient host AsyncLocal state for this source-free
            // maintenance job, and never run its native cleanup inline.
            if (ThreadPool.UnsafeQueueUserWorkItem(
                    static state => ((BitmapOutputPressureTrim)state!).Drain(), this))
                return;
        }
        catch (Exception)
        {
            // Cache reclamation is opportunistic. A later rejection can retry
            // queueing; never fall back to native disposal on this caller.
        }

        Interlocked.Exchange(ref _queued, 0);
    }

    private void Drain()
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref _requested, 0);
                try
                {
                    _clearCache();
                }
                catch (Exception)
                {
                    // The trim is best-effort; a failed worker must not escape
                    // to the ThreadPool or alter the rejected bitmap path.
                }
            }
            while (Volatile.Read(ref _requested) != 0);
        }
        finally
        {
            Interlocked.Exchange(ref _queued, 0);
            if (Volatile.Read(ref _requested) != 0)
                Queue();
        }
    }
}
