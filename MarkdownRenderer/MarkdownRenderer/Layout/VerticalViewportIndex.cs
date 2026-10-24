using System;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Allocation-free lookup over monotonically increasing absolute bottom edges.
/// Layout containers build the edge array off the UI thread and use it to enter
/// directly at the first band that can intersect a viewport.
/// </summary>
internal static class VerticalViewportIndex
{
    public static int FindFirstIntersecting(
        ReadOnlySpan<double> sortedBottomEdges,
        double viewportTop)
    {
        int low = 0;
        int high = sortedBottomEdges.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (sortedBottomEdges[middle] < viewportTop)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }
}
