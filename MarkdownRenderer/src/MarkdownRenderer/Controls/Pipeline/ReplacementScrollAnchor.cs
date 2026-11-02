using System;
using MarkdownRenderer.Layout;
using Microsoft.UI.Xaml.Controls;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// The reader's position before a snapshot replacement: the first visible
/// block and its offset from the viewport top, or only the old offset and
/// extent when the old snapshot could not be read. Restoring it keeps
/// content above the fold from jumping when the canvas height changes.
/// </summary>
/// <param name="BlockIndex">The anchor block, or null for a proportional fallback.</param>
/// <param name="OffsetFromTop">The anchor block's offset from the viewport top.</param>
/// <param name="OldOffset">The vertical scroll offset at capture.</param>
/// <param name="OldHeight">The anchor block height, or the committed extent for the fallback.</param>
internal readonly record struct ReplacementScrollAnchor(
    int? BlockIndex,
    double OffsetFromTop,
    double OldOffset,
    double OldHeight)
{
    /// <summary>
    /// Captures the position in <paramref name="previous"/> when the reader has
    /// scrolled; returns null at the top or without a scroll viewer.
    /// </summary>
    internal static ReplacementScrollAnchor? Capture(LayoutSnapshot? previous, MarkdownViewSurface surface)
    {
        if (surface.Scroll is not { VerticalOffset: > 0 } scroll || previous is null)
            return null;

        double verticalOffset = scroll.VerticalOffset;
        if (previous.TryCaptureScrollAnchor(verticalOffset, out var capturedAnchor))
        {
            return new ReplacementScrollAnchor(
                capturedAnchor.BlockIndex,
                capturedAnchor.OffsetFromTop,
                verticalOffset,
                capturedAnchor.Height);
        }

        // Lazy measurement still owns the old snapshot. Preserve a
        // proportional fallback from the already-committed XAML extent
        // rather than blocking this UI-thread replacement for its lock.
        return new ReplacementScrollAnchor(null, 0, verticalOffset, surface.Root?.Height ?? 0);
    }

    /// <summary>
    /// Scrolls <paramref name="scroll"/> so the anchor block sits where it was
    /// in <paramref name="snapshot"/>, unless the reader moved since capture.
    /// </summary>
    internal void Restore(LayoutSnapshot snapshot, ScrollViewer scroll, bool scrollViewIntermediate)
    {
        if (!LazyLayoutScrollAnchorPolicy.ShouldRestore(
                OldOffset,
                scroll.VerticalOffset,
                scrollViewIntermediate))
        {
            return;
        }

        double? newY = null;
        if (BlockIndex is { } anchorBlock &&
            snapshot.TryCaptureScrollAnchorTarget(
                anchorBlock,
                OffsetFromTop,
                out double capturedTargetOffset))
        {
            newY = capturedTargetOffset;
        }

        if (newY is null)
        {
            double ratio = OldHeight > 0
                ? OldOffset / OldHeight
                : 0;
            newY = ratio > 0
                ? ratio * snapshot.Size.Height
                : OldOffset;
        }

        double maxOffset = Math.Max(0, snapshot.Size.Height - scroll.ViewportHeight);
        scroll.ChangeView(null, Math.Clamp(newY.Value, 0, maxOffset), null, disableAnimation: true);
    }
}
