using System;

namespace MarkdownRenderer.Parsing;

/// <summary>
/// Policy for source-less decorative <c>img</c> tags that act as bounded
/// layout spacers.
/// </summary>
internal static class SafeHtmlInertImageSpacer
{
    /// <summary>
    /// Admits only an explicitly decorative, source-less inline image as a
    /// bounded layout spacer. The two parser entry points share this policy so
    /// inline Markdown HTML and parsed HTML blocks cannot diverge.
    /// </summary>
    internal static bool TryGetDimensions(
        SafeHtmlTag tag,
        bool imagesEnabled,
        bool insidePicture,
        bool insideLink,
        out float width,
        out float height)
    {
        width = 0f;
        height = 0f;
        if (!CanBeInertImageSpacer(tag.Name, imagesEnabled, insidePicture, insideLink))
            return false;

        bool hasAlt = tag.TryGetAttribute("alt", out string alt);
        bool hasSource = tag.TryGetAttribute("src", out _);
        bool hasSourceSet = tag.TryGetAttribute("srcset", out _);
        bool hasLazySource = tag.TryGetAttribute("data-src", out _) ||
            tag.TryGetAttribute("data-srcset", out _) ||
            tag.TryGetAttribute("data-lazy-srcset", out _) ||
            tag.TryGetAttribute("data-original", out _) ||
            tag.TryGetAttribute("data-original-src", out _) ||
            tag.TryGetAttribute("data-original-srcset", out _) ||
            tag.TryGetAttribute("data-lazy-src", out _);
        if (!hasAlt || alt.Length != 0 || hasSource || hasSourceSet || hasLazySource)
            return false;

        bool hasWidth = SafeHtmlAttributeValues.TryGetLength(tag, "width", out SafeHtmlLength parsedWidth);
        bool hasHeight = SafeHtmlAttributeValues.TryGetLength(tag, "height", out SafeHtmlLength parsedHeight);
        return TryValidateInertImageSpacerDimensions(
            hasWidth,
            parsedWidth,
            hasHeight,
            parsedHeight,
            out width,
            out height);
    }

    internal static bool TryGetDimensions(
        SafeHtmlElement element,
        bool imagesEnabled,
        bool insidePicture,
        bool insideLink,
        out float width,
        out float height)
    {
        width = 0f;
        height = 0f;
        if (!CanBeInertImageSpacer(element.Name, imagesEnabled, insidePicture, insideLink))
            return false;

        bool hasAlt = element.TryGetAttribute("alt", out string alt);
        bool hasSource = element.TryGetAttribute("src", out _);
        bool hasSourceSet = element.TryGetAttribute("srcset", out _);
        bool hasLazySource = element.TryGetAttribute("data-src", out _) ||
            element.TryGetAttribute("data-srcset", out _) ||
            element.TryGetAttribute("data-lazy-srcset", out _) ||
            element.TryGetAttribute("data-original", out _) ||
            element.TryGetAttribute("data-original-src", out _) ||
            element.TryGetAttribute("data-original-srcset", out _) ||
            element.TryGetAttribute("data-lazy-src", out _);
        if (!hasAlt || alt.Length != 0 || hasSource || hasSourceSet || hasLazySource)
            return false;

        bool hasWidth = SafeHtmlAttributeValues.TryGetLength(element, "width", out SafeHtmlLength parsedWidth);
        bool hasHeight = SafeHtmlAttributeValues.TryGetLength(element, "height", out SafeHtmlLength parsedHeight);
        return TryValidateInertImageSpacerDimensions(
            hasWidth,
            parsedWidth,
            hasHeight,
            parsedHeight,
            out width,
            out height);
    }

    private static bool CanBeInertImageSpacer(
        string name,
        bool imagesEnabled,
        bool insidePicture,
        bool insideLink)
        => imagesEnabled &&
            !insidePicture &&
            !insideLink &&
            name.Equals("img", StringComparison.OrdinalIgnoreCase);

    private static bool TryValidateInertImageSpacerDimensions(
        bool hasWidth,
        SafeHtmlLength widthLength,
        bool hasHeight,
        SafeHtmlLength heightLength,
        out float width,
        out float height)
    {
        width = 0f;
        height = 0f;
        if (!hasWidth ||
            widthLength.IsPercent ||
            !hasHeight ||
            heightLength.IsPercent ||
            !float.IsFinite(widthLength.Value) ||
            !float.IsFinite(heightLength.Value) ||
            widthLength.Value <= 0f ||
            heightLength.Value <= 0f ||
            widthLength.Value > SafeHtmlParser.MaximumAbsoluteLength ||
            heightLength.Value > SafeHtmlParser.MaximumAbsoluteLength)
        {
            return false;
        }

        width = widthLength.Value;
        height = heightLength.Value;
        return true;
    }
}
