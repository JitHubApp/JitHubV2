using Microsoft.UI.Xaml;

namespace JitHub.Markdown;

/// <summary>
/// Sizes a Markdown host's reading region from the host's own width. The
/// result depends only on the host and its settings, never on the document,
/// so content and notices cannot change the page width.
/// </summary>
public static class MarkdownReadingRegionLayout
{
    private static readonly MarkdownReadingRegionSize Stretch =
        new(double.NaN, double.PositiveInfinity, HorizontalAlignment.Stretch);

    /// <summary>Computes the reading region's width constraints.</summary>
    /// <param name="actualWidth">The host's laid-out width.</param>
    /// <param name="paddingLeft">The host's left content padding.</param>
    /// <param name="paddingRight">The host's right content padding.</param>
    /// <param name="contentMaxWidth">The widest the reading measure may grow, or a non-positive or non-finite value for no limit.</param>
    /// <param name="contentAlignment">Where a width-limited region sits; <see cref="HorizontalAlignment.Stretch"/> centers it.</param>
    /// <returns>The reading region's width constraints.</returns>
    public static MarkdownReadingRegionSize Compute(
        double actualWidth,
        double paddingLeft,
        double paddingRight,
        double contentMaxWidth,
        HorizontalAlignment contentAlignment)
    {
        double availableWidth = actualWidth - paddingLeft - paddingRight;
        if (availableWidth <= 0 || double.IsNaN(availableWidth) || double.IsInfinity(availableWidth))
        {
            return Stretch;
        }

        bool hasFiniteMaxWidth = contentMaxWidth > 0
            && !double.IsNaN(contentMaxWidth)
            && !double.IsInfinity(contentMaxWidth);

        if (hasFiniteMaxWidth && availableWidth > contentMaxWidth)
        {
            return new MarkdownReadingRegionSize(
                contentMaxWidth,
                contentMaxWidth,
                contentAlignment == HorizontalAlignment.Stretch
                    ? HorizontalAlignment.Center
                    : contentAlignment);
        }

        return new MarkdownReadingRegionSize(
            double.NaN,
            hasFiniteMaxWidth ? contentMaxWidth : double.PositiveInfinity,
            HorizontalAlignment.Stretch);
    }
}
