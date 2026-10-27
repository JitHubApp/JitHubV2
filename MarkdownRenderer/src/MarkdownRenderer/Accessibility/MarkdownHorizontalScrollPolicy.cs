using System;

namespace MarkdownRenderer.Accessibility;

internal static class MarkdownHorizontalScrollPolicy
{
    internal static double GetPhysicalPercent(
        double logicalOffset,
        double extent,
        double viewport,
        bool isRightToLeft)
    {
        double maximum = Math.Max(0, extent - viewport);
        if (maximum <= 0)
            return 0;

        double logicalPercent = Math.Clamp(logicalOffset, 0, maximum) * 100.0 / maximum;
        return isRightToLeft ? 100.0 - logicalPercent : logicalPercent;
    }

    internal static double GetLogicalOffset(
        double physicalPercent,
        double extent,
        double viewport,
        bool isRightToLeft)
    {
        double maximum = Math.Max(0, extent - viewport);
        double physicalOffset = maximum * Math.Clamp(physicalPercent, 0, 100) / 100.0;
        return isRightToLeft ? maximum - physicalOffset : physicalOffset;
    }

    internal static double GetLogicalDelta(double physicalDelta, bool isRightToLeft) =>
        isRightToLeft ? -physicalDelta : physicalDelta;
}
