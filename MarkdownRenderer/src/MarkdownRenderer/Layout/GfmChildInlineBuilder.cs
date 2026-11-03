using System.Text;
using System.Collections.Generic;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Abbreviations;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Inline half of <see cref="GfmChildBuilder"/>: builds runs for extension
/// paragraphs and headings with safe-HTML inline state, links, emphasis and
/// images, and flattens inline containers to text.
/// </summary>
internal static class GfmChildInlineBuilder
{
    internal static void AddInlines(
        InlineContainerBox box,
        ContainerInline inlines,
        System.Func<Inline, bool>? skipFirstIf = null,
        int inheritedAliasStart = -1)
    {
        // A custom built-in renderer can fail while visiting later inline
        // nodes, after earlier image/native runs have already been attached.
        // Register the partially populated owner before entering that walk.
        box.Context.RegisterConstructionBlock(box);
        if (box.Context.TryAppendChildInlines(box, inlines, skipFirstIf, inheritedAliasStart))
            return;
        AddInlines(
            box,
            inlines,
            new SafeHtmlInlineState(box.Context.Registry.SafeHtmlPolicy),
            skipFirstIf,
            inheritedAliasStart,
            System.Array.Empty<string>(),
            containingLinkUrl: null,
            containingLinkTitle: null);
    }

    /// <summary>
    /// Adds a paragraph's inlines from <paramref name="bodySourceStart"/> on,
    /// keeping links, emphasis, code and entities. A GitHub alert uses this
    /// for the text after its <c>[!TAG]</c> marker: inlines that end
    /// before the body are the marker; a literal that straddles it is trimmed.
    /// </summary>
    internal static void AddInlinesFromSource(
        InlineContainerBox box,
        ContainerInline inlines,
        int bodySourceStart)
    {
        box.Context.RegisterConstructionBlock(box);
        AddInlines(
            box,
            inlines,
            new SafeHtmlInlineState(box.Context.Registry.SafeHtmlPolicy),
            skipFirstIf: null,
            inheritedAliasStart: -1,
            System.Array.Empty<string>(),
            containingLinkUrl: null,
            containingLinkTitle: null,
            bodySourceStart);
    }

    private static void AddInlines(
        InlineContainerBox box,
        ContainerInline inlines,
        SafeHtmlInlineState htmlState,
        System.Func<Inline, bool>? skipFirstIf,
        int inheritedAliasStart,
        IReadOnlyList<string> inheritedStyleModifiers,
        string? containingLinkUrl,
        string? containingLinkTitle,
        int bodySourceStart = -1)
    {
        bool skippedFirst = skipFirstIf is null;
        foreach (var i in inlines)
        {
            box.Context.CancellationToken.ThrowIfCancellationRequested();
            if (!skippedFirst)
            {
                skippedFirst = true;
                if (skipFirstIf!(i)) continue;
            }
            if (bodySourceStart >= 0)
            {
                if (i.Span.End < bodySourceStart)
                    continue;

                int bodyStart = bodySourceStart;
                bodySourceStart = -1;
                if (i is LiteralInline straddling && straddling.Span.Start < bodyStart)
                {
                    AddBodySuffix(box, straddling, bodyStart);
                    continue;
                }
            }
            AddInline(
                box,
                i,
                htmlState,
                inheritedAliasStart,
                inheritedStyleModifiers,
                containingLinkUrl,
                containingLinkTitle);
        }
    }

    // A literal that straddles the body start contributes only its body part.
    private static void AddBodySuffix(InlineContainerBox box, LiteralInline straddling, int bodyStart)
    {
        string text = straddling.Content.ToString();
        int skip = System.Math.Clamp(bodyStart - straddling.Span.Start, 0, text.Length);
        if (skip < text.Length)
        {
            box.Context.RegisterMarkdownAttributes(straddling, box.BlockIndex);
            box.Add(new TextRun(text.Substring(skip))
            {
                SourceSpan = new MarkdownRenderer.SourceSpan(bodyStart, straddling.Span.End - bodyStart + 1),
            });
        }
    }

    private static void AddInline(
        InlineContainerBox box,
        Inline i,
        SafeHtmlInlineState htmlState,
        int inheritedAliasStart,
        IReadOnlyList<string> inheritedStyleModifiers,
        string? containingLinkUrl,
        string? containingLinkTitle)
    {
        int aliasStart = box.Context.StyleAliasCount;
        using var inlineAttrs = box.Context.PushMarkdownAttributes(i);
        int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
        if (i is EmphasisInline emphasis &&
            (box.Context.ThemeSnapshot.HasRelativeScriptFontSizeRules || MarkdownInlineSyntax.ContainsMarkdownLink(emphasis)))
        {
            box.Context.RegisterMarkdownAttributes(i, box.BlockIndex);
            string emphasisKey = MarkdownInlineSyntax.GetEmphasisElementKey(emphasis);
            using SafeHtmlInlineState.ScriptStyleScope scriptScope =
                htmlState.PushMarkdownScript(emphasisKey);
            AddInlines(
                box,
                emphasis,
                htmlState,
                skipFirstIf: null,
                inheritedAliasStart: effectiveAliasStart,
                inheritedStyleModifiers: MarkdownInlineSyntax.AppendStyleModifier(inheritedStyleModifiers, emphasisKey),
                containingLinkUrl,
                containingLinkTitle);
            return;
        }
        InlineRun? run = ResolveRun(
            box,
            i,
            htmlState,
            containingLinkUrl,
            containingLinkTitle,
            out LinkInline? mixedLink);
        if (mixedLink is not null)
        {
            box.Context.RegisterMarkdownAttributes(i, box.BlockIndex);
            AddInlines(
                box,
                mixedLink,
                htmlState,
                skipFirstIf: null,
                inheritedAliasStart: effectiveAliasStart,
                inheritedStyleModifiers,
                mixedLink.Url,
                mixedLink.Title);
            return;
        }
        if (run is not null)
        {
            AddResolvedRun(box, i, run, inheritedStyleModifiers, effectiveAliasStart);
        }
        else if (i is ContainerInline nested)
        {
            box.Context.RegisterMarkdownAttributes(i, box.BlockIndex);
            AddInlines(
                box,
                nested,
                htmlState,
                skipFirstIf: null,
                inheritedAliasStart: effectiveAliasStart,
                inheritedStyleModifiers: inheritedStyleModifiers,
                containingLinkUrl,
                containingLinkTitle);
        }
    }

    // Resolves one inline to its run. A non-image link that contains a
    // renderable image is returned through mixedLink instead, so its children
    // flow inline under the link's target.
    private static InlineRun? ResolveRun(
        InlineContainerBox box,
        Inline i,
        SafeHtmlInlineState htmlState,
        string? containingLinkUrl,
        string? containingLinkTitle,
        out LinkInline? mixedLink)
    {
        mixedLink = null;
        if (i is HtmlInline html)
        {
            return htmlState.Process(
                html,
                box.Context,
                containingLinkUrl,
                containingLinkTitle);
        }
        if (i is LinkInline link &&
            MarkdownInlineSyntax.TryGetOnlyHtmlImageChild(link, out HtmlInline? linkedHtmlImage) &&
            htmlState.IsStandaloneImage(linkedHtmlImage, box.Context))
        {
            return htmlState.Process(
                linkedHtmlImage,
                box.Context,
                link.Url,
                link.Title,
                MarkdownInlineSyntax.GetLinkedHtmlImageSourceSpan(link, linkedHtmlImage, box.Context));
        }
        if (i is LinkInline candidate &&
            !candidate.IsImage &&
            ContainsRenderableImage(candidate, htmlState, box.Context))
        {
            mixedLink = candidate;
            return null;
        }
        if (!string.IsNullOrWhiteSpace(containingLinkUrl) &&
            i is LinkInline { IsImage: true } containedImage)
        {
            return BuildImageRun(
                containedImage,
                box.Context,
                containingLinkUrl,
                containingLinkTitle,
                containedImage.Span.Start,
                containedImage.Span.Length);
        }
        InlineRun? run = htmlState.Apply(BuildInline(i, box.Context));
        return ApplyContainingLink(run, containingLinkUrl, containingLinkTitle);
    }

    private static void AddResolvedRun(
        InlineContainerBox box,
        Inline i,
        InlineRun run,
        IReadOnlyList<string> inheritedStyleModifiers,
        int effectiveAliasStart)
    {
        if (run is InlineImageRun image)
            box.Context.RegisterConstructionBlock(image.Image);
        run.StyleModifierKeys = MarkdownInlineSyntax.CombineStyleAliases(
            run.StyleModifierKeys,
            inheritedStyleModifiers);
        run.SetStyleAliases(MarkdownInlineSyntax.CombineStyleAliases(
            run.StyleAliases,
            box.Context.CreateStyleAliasSnapshotFrom(effectiveAliasStart)));
        box.Context.RegisterMarkdownAttributes(i, box.BlockIndex);
        box.Add(run);
        if (run is InlineImageRun addedImage)
            box.Context.AdoptConstructionBlock(addedImage.Image);
    }

    private static bool ContainsRenderableImage(
        ContainerInline container,
        SafeHtmlInlineState htmlState,
        MarkdownLayoutContext context)
    {
        foreach (Inline child in container)
        {
            if (child is LinkInline { IsImage: true })
                return true;
            if (child is HtmlInline html && htmlState.IsStandaloneImage(html, context))
                return true;
            if (child is ContainerInline nested && ContainsRenderableImage(nested, htmlState, context))
                return true;
        }

        return false;
    }

    private static InlineRun? ApplyContainingLink(
        InlineRun? run,
        string? containingLinkUrl,
        string? containingLinkTitle)
    {
        if (run is null ||
            run is LinkRun or InlineImageRun or InlineSpacerRun ||
            string.IsNullOrWhiteSpace(containingLinkUrl))
        {
            return run;
        }

        var linked = new LinkRun(run.Text, containingLinkUrl, containingLinkTitle)
        {
            SourceSpan = run.SourceSpan,
            ScriptStyleAncestryKeys = run.ScriptStyleAncestryKeys,
            SafeHtmlScriptRoleIsRunElement = run.SafeHtmlScriptRoleIsRunElement,
        };
        linked.SetStyleAliases(run.StyleAliases);
        linked.StyleModifierKeys = string.IsNullOrEmpty(run.ElementKey)
            ? run.StyleModifierKeys
            : MarkdownInlineSyntax.AppendStyleModifier(run.StyleModifierKeys, run.ElementKey);
        return linked;
    }

    private static InlineRun? BuildInline(Inline inline, MarkdownLayoutContext context) => inline switch
    {
        LiteralInline lit => new TextRun(lit.Content.ToString())
        {
            // No ElementKey = inherit container's style (fixes headings inside GFM blocks).
            SourceSpan = new MarkdownRenderer.SourceSpan(lit.Span.Start, lit.Span.Length)
        },
        CodeInline ci => new CodeInlineRun(ci.Content)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(ci.Span.Start, ci.Span.Length)
        },
        EmphasisInline emph => BuildEmphasis(emph),
        LinkInline link => BuildLink(link, context),
        AbbreviationInline abbreviation => new AbbreviationRun(
            abbreviation.Abbreviation?.Label ?? string.Empty,
            abbreviation.Abbreviation?.Text.ToString() ?? string.Empty)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(abbreviation.Span.Start, abbreviation.Span.Length)
        },
        AutolinkInline al => new LinkRun(al.Url, al.Url)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(al.Span.Start, al.Span.Length)
        },
        LineBreakInline lineBreak => new LineBreakRun(lineBreak.IsHard)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(inline.Span.Start, inline.Span.Length)
        },
        HtmlEntityInline entity => new TextRun(entity.Transcoded.ToString())
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(entity.Span.Start, entity.Span.Length)
        },
        _ => null
    };

    private static InlineRun BuildEmphasis(EmphasisInline emph)
    {
        var sb = new StringBuilder();
        FlattenInlines(emph, sb);
        var span = new MarkdownRenderer.SourceSpan(emph.Span.Start, emph.Span.Length);
        if (emph.DelimiterChar == '~' && emph.DelimiterCount >= 2)
            return new StrikethroughRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '~')
            return new SubscriptRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '^')
            return new SuperscriptRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '+')
            return new InsertedRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '=')
            return new MarkedRun(sb.ToString()) { SourceSpan = span };
        return emph.DelimiterCount >= 2
            ? new StrongRun(sb.ToString()) { SourceSpan = span }
            : new EmphasisRun(sb.ToString()) { SourceSpan = span };
    }

    private static TextRun FlattenAsTextRun(ContainerInline ci)
    {
        var sb = new StringBuilder();
        FlattenInlines(ci, sb);
        return new TextRun(sb.ToString())
        {
            // No ElementKey = inherit container's style.
            SourceSpan = new MarkdownRenderer.SourceSpan(ci.Span.Start, ci.Span.Length)
        };
    }

    private static InlineRun BuildLink(LinkInline link, MarkdownLayoutContext context)
    {
        if (link.IsImage)
        {
            return BuildImageRun(link, context, linkUrl: null, linkTitle: null, link.Span.Start, link.Span.Length);
        }

        if (MarkdownInlineSyntax.TryGetOnlyImageChild(link, out var imageLink))
        {
            return BuildImageRun(imageLink, context, link.Url, link.Title, link.Span.Start, link.Span.Length);
        }

        var text = new StringBuilder();
        FlattenInlines(link, text);
        return new LinkRun(text.ToString(), link.Url ?? string.Empty, link.Title)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(link.Span.Start, link.Span.Length)
        };
    }

    private static InlineImageRun BuildImageRun(
        LinkInline imageLink,
        MarkdownLayoutContext context,
        string? linkUrl,
        string? linkTitle,
        int sourceStart,
        int sourceLength)
    {
        var alt = new StringBuilder();
        FlattenInlines(imageLink, alt);
        SafeHtmlLength? requestedWidth = null;
        SafeHtmlLength? requestedHeight = null;
        if (imageLink is SizedImageLinkInline sizedImage)
        {
            requestedWidth = sizedImage.RequestedWidth;
            requestedHeight = sizedImage.RequestedHeight;
        }

        var run = new InlineImageRun(
            context,
            alt.Length > 0 ? alt.ToString() : "image",
            imageLink.Url ?? string.Empty,
            imageLink.Title,
            linkUrl,
            linkTitle,
            requestedWidth,
            requestedHeight)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(sourceStart, sourceLength)
        };
        context.RegisterConstructionBlock(run.Image);
        return run;
    }

    internal static void FlattenInlines(ContainerInline container, StringBuilder sb)
    {
        foreach (var child in container)
        {
            switch (child)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case CodeInline ci: sb.Append(ci.Content); break;
                case AbbreviationInline ab: sb.Append(ab.Abbreviation?.Label ?? string.Empty); break;
                case LineBreakInline lineBreak: sb.Append(lineBreak.IsHard ? '\n' : ' '); break;
                case HtmlEntityInline entity: sb.Append(entity.Transcoded.ToString()); break;
                case AutolinkInline autolink: sb.Append(autolink.Url); break;
                case LinkInline { IsImage: true } image: FlattenInlines(image, sb); break;
                case ContainerInline c2: FlattenInlines(c2, sb); break;
            }
        }
    }
}
