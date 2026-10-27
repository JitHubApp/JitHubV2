using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal static class PhysicalViewportValidation
{
    internal static bool IsValid(
        MarkdownSvgPhysicalViewport? viewport,
        int targetWidthPixels,
        int targetHeightPixels)
    {
        if (viewport is not { } value)
            return true;

        return targetWidthPixels > 0 &&
            targetHeightPixels > 0 &&
            double.IsFinite(value.Width) &&
            double.IsFinite(value.Height) &&
            value.Width > 0 &&
            value.Height > 0 &&
            Math.Ceiling(value.Width) == targetWidthPixels &&
            Math.Ceiling(value.Height) == targetHeightPixels;
    }
}
