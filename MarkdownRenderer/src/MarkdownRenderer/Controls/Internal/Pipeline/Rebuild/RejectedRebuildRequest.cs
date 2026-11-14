using System;
using System.Threading;
using RebuildReason = MarkdownRenderer.Controls.Internal.Pipeline.Rebuild.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;

/// <summary>
/// The strongest rebuild request whose cross-thread dispatch the UI queue
/// rejected. Recording is lock-free from any thread; the UI thread takes the
/// request back and retries it.
/// </summary>
internal sealed class RejectedRebuildRequest
{
    private int _reason = -1;

    /// <summary>Merges <paramref name="reason"/> into the remembered request; any thread.</summary>
    internal void Record(RebuildReason reason)
    {
        int requested = (int)reason;
        while (true)
        {
            int current = Volatile.Read(ref _reason);
            int merged = Math.Max(current, requested);
            if (Interlocked.CompareExchange(
                    ref _reason,
                    merged,
                    current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Takes the remembered request, if any, and forgets it.</summary>
    internal bool TryTake(out RebuildReason reason)
    {
        int taken = Interlocked.Exchange(ref _reason, -1);
        reason = (RebuildReason)taken;
        return taken >= 0;
    }

    internal void Reset() => Interlocked.Exchange(ref _reason, -1);
}
