using System;

namespace JitHub.WinUI.Helpers;

internal static class ShyHeaderScrollPolicy
{
    public static bool TryGetOverlayOffsets(
        double sourceTop,
        double sourceHeight,
        double overlayHeight,
        double restoreInset,
        out double startOffset,
        out double restoreOffset)
    {
        startOffset = 0;
        restoreOffset = 0;
        if (!double.IsFinite(sourceTop) ||
            !double.IsFinite(sourceHeight) ||
            !double.IsFinite(overlayHeight) ||
            !double.IsFinite(restoreInset) ||
            sourceHeight <= 0 ||
            overlayHeight < 0 ||
            restoreInset < 0)
        {
            return false;
        }

        // The expanded surface keeps its layout slot while the compact surface
        // overlays it. Collapse only when the expanded bottom reaches the bottom
        // of that overlay, then use a small reverse-scroll hysteresis to restore.
        startOffset = Math.Max(0, sourceTop + sourceHeight - overlayHeight);
        restoreOffset = Math.Max(0, startOffset - restoreInset);
        return true;
    }

    public static bool CanCollapse(
        double scrollableHeight,
        double expandedHeaderHeight,
        double restoreOffset)
    {
        if (!double.IsFinite(scrollableHeight) ||
            !double.IsFinite(expandedHeaderHeight) ||
            !double.IsFinite(restoreOffset))
        {
            return false;
        }

        return scrollableHeight > Math.Max(0, expandedHeaderHeight) + Math.Max(0, restoreOffset);
    }
}
