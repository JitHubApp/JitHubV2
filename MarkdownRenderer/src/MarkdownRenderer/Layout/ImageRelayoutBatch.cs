using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Takes a bounded, source-free snapshot of pending image block indices.
/// Capturing never consumes the caller's pending set; the control removes only
/// the returned indices after it has accepted the corresponding worker.
/// </summary>
internal static class ImageRelayoutBatch
{
    internal static int[] CapturePendingIndices(
        IReadOnlyCollection<int> pending,
        bool optedIn)
    {
        ArgumentNullException.ThrowIfNull(pending);

        int count = optedIn
            ? Math.Min(pending.Count, LayoutSnapshot.MaximumPreparedImageRoots)
            : pending.Count;
        if (count == 0)
            return Array.Empty<int>();

        var captured = new int[count];
        int copied = 0;
        foreach (int blockIndex in pending)
        {
            if (copied == count)
                break;
            captured[copied++] = blockIndex;
        }

        // The control owns this set on its UI thread, so its count cannot
        // change during capture. Retain a fail-soft path for other internal
        // callers rather than publishing default array entries if it does.
        if (copied != captured.Length)
            Array.Resize(ref captured, copied);

        // Root-local preparation consumes roots in document order. Sorting is
        // limited to the bounded opt-in batch; legacy sessionless capture keeps
        // the prior collection enumeration behavior and size.
        if (optedIn && captured.Length > 1)
            Array.Sort(captured);

        return captured;
    }
}
