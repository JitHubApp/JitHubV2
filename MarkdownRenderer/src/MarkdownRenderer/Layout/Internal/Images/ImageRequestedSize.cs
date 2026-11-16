using System;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Layout.Internal.Images;

/// <summary>
/// Applies an image's authored width and height to its natural size: both
/// dimensions scale together to fit the available width, one dimension keeps
/// the natural aspect ratio, and a percentage height is ignored.
/// </summary>
internal static class ImageRequestedSize
{
    /// <summary>Replaces <paramref name="width"/> and <paramref name="height"/> with the authored size.</summary>
    internal static void Apply(
        SafeHtmlLength? requestedWidthLength,
        SafeHtmlLength? requestedHeightLength,
        ref float width,
        ref float height,
        float availableWidth)
    {
        float naturalWidth = Math.Max(1f, width);
        float naturalHeight = Math.Max(1f, height);
        float aspect = naturalWidth / naturalHeight;
        float? requestedWidth = requestedWidthLength?.Resolve(availableWidth);
        float? requestedHeight = requestedHeightLength is { IsPercent: false } heightLength
            ? heightLength.Resolve(availableWidth)
            : null;

        if (requestedWidth is > 0 && requestedHeight is > 0)
        {
            float scale = Math.Min(1f, availableWidth / requestedWidth.Value);
            width = Math.Max(1f, requestedWidth.Value * scale);
            height = Math.Max(1f, requestedHeight.Value * scale);
            return;
        }

        if (requestedWidth is > 0)
        {
            width = Math.Max(1f, Math.Min(availableWidth, requestedWidth.Value));
            height = Math.Max(1f, width / Math.Max(0.001f, aspect));
            return;
        }

        if (requestedHeight is > 0)
        {
            height = Math.Max(1f, requestedHeight.Value);
            width = Math.Max(1f, height * aspect);
            if (width > availableWidth)
            {
                float scale = availableWidth / width;
                width = availableWidth;
                height = Math.Max(1f, height * scale);
            }
        }
    }
}
