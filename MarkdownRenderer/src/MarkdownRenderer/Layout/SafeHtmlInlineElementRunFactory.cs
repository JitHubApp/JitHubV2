using System;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Creates the leaf runs that supported inline safe-HTML elements produce
/// directly (line breaks, read-only task checkboxes, media and images). Each
/// run inherits the enclosing scopes of the supplied
/// <see cref="SafeHtmlInlineState"/>: its link, picture ancestry, style and
/// class aliases.
/// </summary>
internal static class SafeHtmlInlineElementRunFactory
{
    internal static InlineRun? CreateElementRun(
        SafeHtmlInlineState state,
        SafeHtmlRenderPolicy policy,
        in SafeHtmlTag tag,
        MarkdownLayoutContext context,
        SourceSpan span,
        string? containingLinkUrl,
        string? containingLinkTitle)
    {
        if (tag.Name == "br")
        {
            return new LineBreakRun(isHard: true) { SourceSpan = span };
        }

        if (tag.Name == "hr")
        {
            return new LineBreakRun(isHard: true) { SourceSpan = span };
        }

        if (tag.Name == "input")
        {
            if (!tag.TryGetAttribute("type", out string type) ||
                !type.Equals("checkbox", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return TaskMarkerControlFactory.CreateReadOnlyRun(
                context,
                tag.TryGetAttribute("checked", out _),
                span);
        }

        if (tag.Name is "video" or "audio")
        {
            return CreateMediaRun(state, policy, tag, context, span, containingLinkUrl, containingLinkTitle);
        }

        if (tag.Name != "img")
        {
            return null;
        }

        return CreateImageRun(state, policy, tag, context, span, containingLinkUrl, containingLinkTitle);
    }

    private static InlineRun CreateMediaRun(
        SafeHtmlInlineState state,
        SafeHtmlRenderPolicy policy,
        in SafeHtmlTag tag,
        MarkdownLayoutContext context,
        SourceSpan span,
        string? containingLinkUrl,
        string? containingLinkTitle)
    {
        SafeHtmlParser.TryGetSafeImageSource(tag, out string mediaSource);
        string? posterSource = tag.TryGetAttribute("poster", out string poster) &&
            SafeHtmlParser.TryNormalizeImageSource(poster, out string safePoster)
                ? safePoster
                : null;
        string? accessibilityName = tag.TryGetAttribute("aria-label", out string ariaLabel) &&
            !string.IsNullOrWhiteSpace(ariaLabel)
                ? ariaLabel
                : tag.TryGetAttribute("title", out string mediaTitle) &&
                    !string.IsNullOrWhiteSpace(mediaTitle)
                        ? mediaTitle
                        : null;
        SafeHtmlLength? mediaWidth = SafeHtmlParser.TryGetLength(tag, "width", out SafeHtmlLength parsedWidth)
            ? parsedWidth
            : null;
        SafeHtmlLength? mediaHeight = SafeHtmlParser.TryGetLength(tag, "height", out SafeHtmlLength parsedHeight)
            ? parsedHeight
            : null;
        SafeHtmlInlineScope? containingScope = state.FindLinkScope();
        return state.ApplyAliases(SafeHtmlMediaRunFactory.Create(
            context,
            tag.Name == "video",
            accessibilityName,
            mediaSource,
            posterSource,
            mediaWidth,
            mediaHeight,
            span,
            policy.EnableLinks ? containingLinkUrl ?? containingScope?.LinkUrl : null,
            policy.EnableLinks ? containingLinkTitle ?? containingScope?.LinkTitle : null));
    }

    private static InlineRun? CreateImageRun(
        SafeHtmlInlineState state,
        SafeHtmlRenderPolicy policy,
        in SafeHtmlTag tag,
        MarkdownLayoutContext context,
        SourceSpan span,
        string? containingLinkUrl,
        string? containingLinkTitle)
    {
        if (SafeHtmlParser.TryGetInertImageSpacerDimensions(
                tag,
                policy.EnableImages,
                state.IsInsidePicture(),
                !string.IsNullOrWhiteSpace(containingLinkUrl) || state.IsInsideLinkElement(),
                out float spacerWidth,
                out float spacerHeight))
        {
            return new InlineSpacerRun(spacerWidth, spacerHeight) { SourceSpan = span };
        }

        bool hasExplicitAlt = tag.TryGetAttribute("alt", out string altValue);
        string alt = hasExplicitAlt
            ? altValue
            : SafeHtmlParser.ResolveMissingImageAlternative(
                tag.TryGetAttribute("src", out string sourceAttribute) ? sourceAttribute : string.Empty,
                context.ResolveString(MarkdownStringKeys.ImageName, MarkdownLocalizedStrings.ImageName));
        if (!policy.EnableImages)
        {
            InlineRun? fallback = string.IsNullOrWhiteSpace(alt)
                ? null
                : state.Apply(new TextRun(alt) { SourceSpan = span });
            return ApplyContainingLink(state, policy, fallback, containingLinkUrl, containingLinkTitle);
        }

        if (!SafeHtmlParser.TryGetSafeImageSource(tag, out string source))
        {
            InlineRun? fallback = string.IsNullOrWhiteSpace(alt)
                ? null
                : state.Apply(new TextRun(alt) { SourceSpan = span });
            return ApplyContainingLink(state, policy, fallback, containingLinkUrl, containingLinkTitle);
        }

        tag.TryGetAttribute("title", out string title);
        SafeHtmlParser.TryGetLength(tag, "width", out SafeHtmlLength width);
        SafeHtmlParser.TryGetLength(tag, "height", out SafeHtmlLength height);
        SafeHtmlInlineScope? link = state.FindLinkScope();
        return state.ApplyAliases(new InlineImageRun(
            context,
            alt,
            source,
            string.IsNullOrWhiteSpace(title) ? null : title,
            policy.EnableLinks ? containingLinkUrl ?? link?.LinkUrl : null,
            policy.EnableLinks ? containingLinkTitle ?? link?.LinkTitle : null,
            width.Value > 0 ? width : null,
            height.Value > 0 ? height : null)
        {
            SourceSpan = span,
        });
    }

    private static InlineRun? ApplyContainingLink(
        SafeHtmlInlineState state,
        SafeHtmlRenderPolicy policy,
        InlineRun? run,
        string? containingLinkUrl,
        string? containingLinkTitle)
    {
        if (run is null ||
            !policy.EnableLinks ||
            string.IsNullOrWhiteSpace(containingLinkUrl) ||
            run is LinkRun or InlineSpacerRun)
        {
            return run;
        }

        return state.ApplyAliases(new LinkRun(run.Text, containingLinkUrl, containingLinkTitle)
        {
            SourceSpan = run.SourceSpan,
        });
    }
}
