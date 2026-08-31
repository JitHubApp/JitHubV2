using System;

namespace JitHub.WinUI.Helpers;

internal static class ShyHeaderScrollPolicy
{
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
