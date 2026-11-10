using System;
using System.Linq;
using System.Text;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.SafeHtml;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// Creates media, inline-SVG and image-size runs for safe-HTML inline content.
/// </summary>
internal static class HtmlMediaRuns
{
    internal static void AddMediaRun(
        InlineContainerBox box,
        SafeHtmlElement media,
        MarkdownLayoutContext context,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        SafeHtmlElement sourceElement = media;
        if (!SafeHtmlParser.TryGetSafeImageSource(sourceElement, out string mediaSource))
        {
            sourceElement = HtmlElementQueries.FindDescendant(media, "source") ?? media;
            if (!SafeHtmlParser.TryGetSafeImageSource(sourceElement, out mediaSource))
                mediaSource = string.Empty;
        }

        string? accessibilityName = media.TryGetAttribute("aria-label", out string ariaLabel) &&
            !string.IsNullOrWhiteSpace(ariaLabel)
                ? ariaLabel.Trim()
                : media.TryGetAttribute("title", out string title) && !string.IsNullOrWhiteSpace(title)
                    ? title.Trim()
                    : null;
        bool isVideo = media.Name == "video";
        SafeHtmlLength? width = SafeHtmlParser.TryGetLength(media, "width", out SafeHtmlLength parsedWidth)
            ? parsedWidth
            : null;
        SafeHtmlLength? height = SafeHtmlParser.TryGetLength(media, "height", out SafeHtmlLength parsedHeight)
            ? parsedHeight
            : null;
        string? posterSource = media.TryGetAttribute("poster", out string poster) &&
            SafeHtmlParser.TryNormalizeImageSource(poster, out string safePoster)
                ? safePoster
                : null;
        InlineImageRun run = SafeHtmlMediaRunFactory.Create(
            context,
            isVideo,
            accessibilityName,
            mediaSource,
            posterSource,
            width,
            height,
            context.MapSafeHtmlSourceRange(sourceOffset + media.SourceStart, media.SourceLength),
            inlineContext.LinkUrl,
            inlineContext.LinkTitle);
        run.SetStyleAliases(inlineContext.StyleAliases);
        HtmlBoxes.AddRun(box, run);
    }

    internal static void AddInlineSvgRun(
        InlineContainerBox box,
        SafeHtmlElement svg,
        MarkdownLayoutContext context,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        // Inline SVG is inert image content, not HTML UI. Preserve the exact
        // authored bytes and route them through the same bounded SVG preflight
        // and isolated renderer as ordinary Markdown images. This keeps script,
        // foreignObject, nested network/file references, and resource bombs
        // subject to one security policy instead of duplicating an SVG parser
        // in the safe-HTML layer.
        if (svg.RawOpeningTag.Length == 0 || svg.RawClosingTag.Length == 0)
            return;

        var markup = new StringBuilder(Math.Max(64, svg.SourceLength));
        AppendRawMarkup(markup, svg);
        if (markup.Length == 0)
            return;

        string dataUri = "data:image/svg+xml;base64," +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(markup.ToString()));
        string accessibilityName = GetSvgAccessibilityName(svg);
        SafeHtmlLength? width = SafeHtmlParser.TryGetLength(svg, "width", out SafeHtmlLength parsedWidth)
            ? parsedWidth
            : null;
        SafeHtmlLength? height = SafeHtmlParser.TryGetLength(svg, "height", out SafeHtmlLength parsedHeight)
            ? parsedHeight
            : null;
        var run = new InlineImageRun(
            context,
            accessibilityName,
            dataUri,
            title: null,
            inlineContext.LinkUrl,
            inlineContext.LinkTitle,
            width,
            height)
        {
            SourceSpan = context.MapSafeHtmlSourceRange(sourceOffset + svg.SourceStart, svg.SourceLength),
        };
        run.SetStyleAliases(inlineContext.StyleAliases);
        HtmlBoxes.AddRun(box, run);
    }

    private static void AppendRawMarkup(StringBuilder destination, SafeHtmlElement element)
    {
        destination.Append(element.RawOpeningTag);
        if (element.RawTrailingMarkup.Length > 0)
        {
            destination.Append(element.RawTrailingMarkup);
            return;
        }

        foreach (SafeHtmlNode child in element.Children)
        {
            if (child is SafeHtmlText text)
                destination.Append(text.RawText);
            else if (child is SafeHtmlElement nested)
                AppendRawMarkup(destination, nested);
        }
        destination.Append(element.RawClosingTag);
    }

    private static string GetSvgAccessibilityName(SafeHtmlElement svg)
    {
        if (svg.TryGetAttribute("aria-label", out string ariaLabel) &&
            !string.IsNullOrWhiteSpace(ariaLabel))
        {
            return ariaLabel.Trim();
        }

        SafeHtmlElement? title = HtmlElementQueries.FindDescendant(svg, "title");
        return title is null
            ? string.Empty
            : string.Concat(HtmlElementQueries.DescendantText(title).Select(static text => text.DecodedText)).Trim();
    }

    internal static SafeHtmlLength? GetImageLength(
        SafeHtmlElement? source,
        SafeHtmlElement image,
        string name)
    {
        if (source is not null && SafeHtmlParser.TryGetLength(source, name, out SafeHtmlLength sourceLength))
        {
            return sourceLength;
        }

        return SafeHtmlParser.TryGetLength(image, name, out SafeHtmlLength imageLength)
            ? imageLength
            : null;
    }
}
