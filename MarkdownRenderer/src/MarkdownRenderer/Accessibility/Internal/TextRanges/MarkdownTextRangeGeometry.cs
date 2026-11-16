using System.Collections.Generic;

namespace MarkdownRenderer.Accessibility.Internal.TextRanges;

/// <summary>
/// Converts a range's screen rectangles into the UIA bounding-rectangle array
/// and picks the rectangle ScrollIntoView targets.
/// </summary>
internal static class MarkdownTextRangeGeometry
{
    internal static double[] BuildVisibleBoundingRectangles(
        int start,
        int end,
        IEnumerable<Windows.Foundation.Rect> screenRects,
        Windows.Foundation.Rect visibleBounds)
    {
        return AccessibilityGeometry.BuildVisibleBoundingRectangles(
            start,
            end,
            ConvertRects(),
            new AccessibilityRect(
                visibleBounds.X,
                visibleBounds.Y,
                visibleBounds.Width,
                visibleBounds.Height));

        IEnumerable<AccessibilityRect> ConvertRects()
        {
            foreach (var rect in screenRects)
                yield return new AccessibilityRect(rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    internal static bool TryGetFirstNonEmptyScrollRect(
        IEnumerable<Windows.Foundation.Rect> documentRects,
        out Windows.Foundation.Rect scrollRect)
    {
        foreach (var rect in documentRects)
        {
            // Clipped semantic content can have no glyph bounds. Preserve the
            // block-position fallback instead of scrolling to an empty sentinel;
            // finite zero-width caret rectangles remain valid scroll targets.
            if (!rect.IsEmpty)
            {
                scrollRect = rect;
                return true;
            }
        }

        scrollRect = default;
        return false;
    }
}
