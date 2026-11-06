using System;
using System.Collections.Generic;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Html.Renderers;

/// <summary>
/// Builds the inline runs of safe-HTML content under the configured
/// image, link, class and unknown-element policies.
/// </summary>
internal sealed class HtmlInlineBuilder
{
    private readonly SafeHtmlOptions _options;

    internal HtmlInlineBuilder(SafeHtmlOptions options)
    {
        _options = options;
    }

    internal void PopulateInline(
        InlineContainerBox box,
        IReadOnlyList<SafeHtmlNode> nodes,
        MarkdownLayoutContext context,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        if (inlineContext.SourceProjection is null && context.SourceProjections.SafeHtml is { } sourceProjection)
            inlineContext = inlineContext with { SourceProjection = sourceProjection };

        foreach (SafeHtmlNode node in nodes)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case SafeHtmlText text:
                    HtmlTextRuns.AddTextRun(box, text, sourceOffset, inlineContext);
                    break;
                case SafeHtmlElement element:
                    AddInlineElement(box, element, context, sourceOffset, inlineContext);
                    break;
            }
        }
    }

    private void AddInlineElement(
        InlineContainerBox box,
        SafeHtmlElement element,
        MarkdownLayoutContext context,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        if (TryAddPolicyElement(box, element, context, sourceOffset, inlineContext))
            return;

        var span = context.MapSafeHtmlSourceRange(sourceOffset + element.SourceStart, element.SourceLength);
        if (TryAddAtomicElement(box, element, span, context, sourceOffset, inlineContext))
            return;

        HtmlInlineContext childContext = CreateChildContext(element, inlineContext);
        bool blockBoundary = HtmlElementNames.IsBlockElement(element.Name);
        if (blockBoundary && box.Runs.Count > 0 && box.Runs[^1] is not LineBreakRun)
        {
            box.Add(new LineBreakRun(isHard: true) { SourceSpan = span });
        }

        PopulateInline(box, element.Children, context, sourceOffset, childContext);

        if (blockBoundary && box.Runs.Count > 0 && box.Runs[^1] is not LineBreakRun)
        {
            box.Add(new LineBreakRun(isHard: true) { SourceSpan = span });
        }
    }

    /// <summary>
    /// Handles the elements whose treatment is fixed by policy before any
    /// supported-element rendering: literal script, suppressed and form
    /// elements, checkbox inputs, and unknown elements.
    /// </summary>
    private bool TryAddPolicyElement(
        InlineContainerBox box,
        SafeHtmlElement element,
        MarkdownLayoutContext context,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        // The default literal policy exposes script markup as inert source
        // for inspection. It is ordinary text: do not carry link or
        // styling semantics from an enclosing element into the fallback.
        // RawContentElements keeps the script body opaque to the parser, and
        // AddLiteralElement emits only its original source; no script is run.
        if (_options.UnknownElementBehavior == SafeHtmlUnknownElementBehavior.RenderLiteral &&
            string.Equals(element.Name, "script", StringComparison.OrdinalIgnoreCase))
        {
            HtmlTextRuns.AddLiteralElement(
                box,
                element,
                sourceOffset,
                HtmlInlineContext.Empty with { SourceProjection = inlineContext.SourceProjection });
            return true;
        }

        if (SafeHtmlParser.IsSuppressedElement(element.Name) || element.Name is "form" or "button")
        {
            return true;
        }

        if (element.Name == "input")
        {
            AddInputRun(box, element, context, sourceOffset);
            return true;
        }

        if (!HtmlElementNames.IsSupportedElement(element.Name))
        {
            if (_options.UnknownElementBehavior == SafeHtmlUnknownElementBehavior.RenderLiteral)
                HtmlTextRuns.AddLiteralElement(box, element, sourceOffset, inlineContext);
            else
                PopulateInline(box, element.Children, context, sourceOffset, inlineContext);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Handles the supported elements that render as one atomic run (or as
    /// nothing) rather than as styled children.
    /// </summary>
    private bool TryAddAtomicElement(
        InlineContainerBox box,
        SafeHtmlElement element,
        SourceSpan span,
        MarkdownLayoutContext context,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        if (element.Name == "br")
        {
            box.Add(new LineBreakRun(isHard: true) { SourceSpan = span });
            return true;
        }

        if (element.Name == "hr")
        {
            box.Add(new LineBreakRun(isHard: true) { SourceSpan = span });
            return true;
        }

        if (element.Name == "pre")
        {
            AddPreformattedInline(box, element, context, sourceOffset, inlineContext);
            return true;
        }

        if (element.Name is "img" or "picture")
        {
            AddImageElement(box, element, context, sourceOffset, inlineContext);
            return true;
        }

        if (element.Name is "video" or "audio")
        {
            HtmlMediaRuns.AddMediaRun(box, element, context, sourceOffset, inlineContext);
            return true;
        }

        if (element.Name == "svg")
        {
            // GitHub appends a decorative permalink Octicon to every rendered
            // heading. It is explicitly excluded from the accessibility tree
            // and is browser chrome rather than README content; materializing
            // it as an atomic linked image adds a visible replacement glyph
            // and a duplicate hyperlink to the native document.
            bool isAriaHidden = element.TryGetAttribute("aria-hidden", out string ariaHidden) &&
                ariaHidden.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (_options.EnableImages && !isAriaHidden)
                HtmlMediaRuns.AddInlineSvgRun(box, element, context, sourceOffset, inlineContext);
            return true;
        }

        return element.Name == "source";
    }

    private void AddImageElement(
        InlineContainerBox box,
        SafeHtmlElement element,
        MarkdownLayoutContext context,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        if (!_options.EnableImages)
        {
            SafeHtmlElement? image = element.Name == "img" ? element : HtmlElementQueries.FindDescendant(element, "img");
            if (image is not null &&
                image.TryGetAttribute("alt", out string disabledAlt) &&
                !string.IsNullOrWhiteSpace(disabledAlt))
            {
                HtmlTextRuns.AddDecodedTextRun(
                    box,
                    disabledAlt,
                    context.MapSafeHtmlSourceRange(sourceOffset + image.SourceStart, image.SourceLength),
                    inlineContext);
            }

            return;
        }

        AddImageRun(box, element, context, sourceOffset, inlineContext);
    }

    /// <summary>
    /// Derives the inline state a supported styled element passes to its
    /// children: class aliases, a safe link target, anchor membership, the
    /// style key, and heading semantics.
    /// </summary>
    private HtmlInlineContext CreateChildContext(SafeHtmlElement element, HtmlInlineContext inlineContext)
    {
        HtmlInlineContext childContext = inlineContext;
        IReadOnlyList<string> classAliases = GetAllowedClassAliases(element);
        if (classAliases.Count > 0)
        {
            childContext = childContext with
            {
                StyleAliases = CombineAliases(childContext.StyleAliases, classAliases),
            };
        }

        if (_options.EnableLinks &&
            element.Name == "a" &&
            SafeHtmlParser.TryGetSafeLink(element, out string href))
        {
            element.TryGetAttribute("title", out string title);
            childContext = childContext with
            {
                LinkUrl = href,
                LinkTitle = string.IsNullOrWhiteSpace(title) ? null : title,
            };
        }

        if (element.Name.Equals("a", StringComparison.OrdinalIgnoreCase))
        {
            // Link policy controls interactivity, not whether content occurred
            // inside an authored anchor. Keep that context so a source-less
            // decorative image is not reclassified as layout-only when links
            // are disabled or the href is unsafe.
            childContext = childContext with { InsideLink = true };
        }

        string? styleKey = SafeHtmlInlineElementNames.GetStyleKey(element.Name);
        if (!string.IsNullOrEmpty(styleKey))
        {
            childContext = childContext with { StyleKey = styleKey };
        }

        string? headingKey = HtmlElementNames.HeadingKey(element.Name);
        if (!string.IsNullOrEmpty(headingKey))
            childContext = childContext with { HeadingKey = headingKey };

        return childContext;
    }

    private static void AddPreformattedInline(
        InlineContainerBox box,
        SafeHtmlElement pre,
        MarkdownLayoutContext context,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        HtmlPreformattedTextProjection? textProjection = HtmlPreformattedTextProjection.Create(
            pre,
            context,
            sourceOffset);
        SourceSpan span = textProjection?.SourceSpan ?? SourceSpan.Empty;
        if (box.Runs.Count > 0 && box.Runs[^1] is not LineBreakRun)
            box.Add(new LineBreakRun(isHard: true) { SourceSpan = span });

        string text = textProjection?.Text ?? string.Empty;
        if (text.Length > 0)
        {
            var run = new TextRun(text)
            {
                ElementKey = MarkdownElementKeys.CodeBlock,
                SourceSpan = span,
                SemanticHeadingKey = inlineContext.HeadingKey ?? string.Empty,
            };
            run.SetStyleAliases(inlineContext.StyleAliases);
            box.Add(run, textProjection!.SourceProjection!);
        }

        if (box.Runs.Count > 0 && box.Runs[^1] is not LineBreakRun)
            box.Add(new LineBreakRun(isHard: true) { SourceSpan = span });
    }

    private void AddImageRun(
        InlineContainerBox box,
        SafeHtmlElement element,
        MarkdownLayoutContext context,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        SafeHtmlElement? image = element.Name == "img" ? element : HtmlElementQueries.FindDescendant(element, "img");
        if (image is null)
        {
            return;
        }

        if (element.Name.Equals("img", StringComparison.OrdinalIgnoreCase) &&
            SafeHtmlParser.TryGetInertImageSpacerDimensions(
                image,
                imagesEnabled: _options.EnableImages,
                insidePicture: false,
                insideLink: inlineContext.InsideLink || !string.IsNullOrWhiteSpace(inlineContext.LinkUrl),
                out float spacerWidth,
                out float spacerHeight))
        {
            HtmlBoxes.AddRun(box, new InlineSpacerRun(spacerWidth, spacerHeight)
            {
                SourceSpan = context.MapSafeHtmlSourceRange(sourceOffset + image.SourceStart, image.SourceLength),
                SemanticHeadingKey = inlineContext.HeadingKey ?? string.Empty,
            });
            return;
        }

        SafeHtmlElement? selectedSource = element.Name == "picture"
            ? HtmlPictureSources.SelectPictureSource(element, context.ThemeSnapshot.IsDark)
            : null;
        string source = string.Empty;
        if (selectedSource is not null)
            HtmlPictureSources.TryGetSinglePictureSourceCandidate(selectedSource, out source);

        if (source.Length == 0 && !SafeHtmlParser.TryGetSafeImageSource(image, out source))
        {
            if (image.TryGetAttribute("alt", out string unavailableAlt) && !string.IsNullOrWhiteSpace(unavailableAlt))
            {
                HtmlTextRuns.AddDecodedTextRun(
                    box,
                    unavailableAlt,
                    context.MapSafeHtmlSourceRange(sourceOffset + image.SourceStart, image.SourceLength),
                    inlineContext);
            }

            return;
        }

        bool hasExplicitAlt = image.TryGetAttribute("alt", out string alt);
        image.TryGetAttribute("title", out string title);
        SafeHtmlLength? width = HtmlMediaRuns.GetImageLength(selectedSource, image, "width");
        SafeHtmlLength? height = HtmlMediaRuns.GetImageLength(selectedSource, image, "height");
        var run = new InlineImageRun(
            context,
            !hasExplicitAlt
                ? SafeHtmlParser.ResolveMissingImageAlternative(
                    source,
                    context.ResolveString(MarkdownStringKeys.ImageName, MarkdownLocalizedStrings.ImageName))
                : alt,
            source,
            string.IsNullOrWhiteSpace(title) ? null : title,
            inlineContext.LinkUrl,
            inlineContext.LinkTitle,
            width,
            height)
        {
            SourceSpan = context.MapSafeHtmlSourceRange(sourceOffset + image.SourceStart, image.SourceLength),
            SemanticHeadingKey = inlineContext.HeadingKey ?? string.Empty,
        };
        run.SetStyleAliases(inlineContext.StyleAliases);
        HtmlBoxes.AddRun(box, run);
    }

    private static void AddInputRun(
        InlineContainerBox box,
        SafeHtmlElement input,
        MarkdownLayoutContext context,
        int sourceOffset)
    {
        if (!input.TryGetAttribute("type", out string type) ||
            !type.Equals("checkbox", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool isChecked = input.TryGetAttribute("checked", out _);
        var sourceRange = context.MapSafeHtmlSourceRange(sourceOffset + input.SourceStart, input.SourceLength);
        box.Add(TaskMarkerControlFactory.CreateReadOnlyRun(
            context,
            isChecked,
            sourceRange));
    }

    internal IReadOnlyList<string> GetAllowedClassAliases(SafeHtmlElement element)
    {
        if (_options.AllowedStyleClasses.Count == 0 ||
            !element.TryGetAttribute("class", out string value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        var aliases = new List<string>();
        foreach (string token in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (_options.AllowedStyleClasses.Contains(token))
                aliases.Add(MarkdownElementKeys.Class(token));
        }

        return aliases.Count == 0 ? Array.Empty<string>() : aliases.ToArray();
    }

    private static IReadOnlyList<string> CombineAliases(
        IReadOnlyList<string> inherited,
        IReadOnlyList<string> local)
    {
        if (inherited.Count == 0)
            return local;
        if (local.Count == 0)
            return inherited;

        var combined = new string[inherited.Count + local.Count];
        for (int index = 0; index < inherited.Count; index++)
            combined[index] = inherited[index];
        for (int index = 0; index < local.Count; index++)
            combined[inherited.Count + index] = local[index];
        return combined;
    }
}
