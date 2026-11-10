using System;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls;

internal static class LazyLayoutScrollAnchorPolicy
{
    internal static bool ShouldRestore(
        double? capturedOffset,
        double? currentOffset,
        bool scrollInProgress) =>
        !scrollInProgress &&
        capturedOffset is { } captured &&
        currentOffset is { } current &&
        double.IsFinite(captured) &&
        double.IsFinite(current) &&
        Math.Abs(current - captured) < 0.5;

    /// <summary>
    /// The scroll offset that puts <paramref name="anchor"/>'s block back at
    /// its captured distance from the viewport top, or null when there is no
    /// anchor or its block is not in <paramref name="snapshot"/>.
    /// </summary>
    internal static double? GetTargetOffset(LayoutSnapshot snapshot, (int BlockIndex, double OffsetFromTop)? anchor)
    {
        if (anchor is not { } value)
            return null;

        foreach (var b in snapshot.Blocks)
        {
            if (b.BlockIndex != value.BlockIndex)
                continue;

            return snapshot.TryGetLogicalTopForBlockIndex(value.BlockIndex, out double logicalTop)
                ? Math.Max(0, logicalTop - value.OffsetFromTop)
                : null;
        }
        return null;
    }
}
