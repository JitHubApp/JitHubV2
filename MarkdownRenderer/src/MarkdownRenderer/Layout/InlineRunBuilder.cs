using System;
using System.Collections.Generic;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.Footnotes;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Builds inline runs for paragraphs, headings and extension containers,
/// including safe-HTML inline state, links, emphasis and inline images.
/// </summary>
internal sealed class InlineRunBuilder
{
    private readonly MarkdownLayoutContext _context;
    private readonly MarkdownRenderer.Document.MarkdownDocument? _semanticDocument;
    private readonly LayoutConstructionTracker _tracker;
    private readonly DeclarativeInlineBuilder _declarativeInlines;
    private readonly InlineContainerFlattener _flattener;

    internal InlineRunBuilder(
        MarkdownLayoutContext context,
        MarkdownRenderer.Document.MarkdownDocument? semanticDocument,
        LayoutConstructionTracker tracker,
        DeclarativeInlineBuilder declarativeInlines,
        InlineContainerFlattener flattener)
    {
        _context = context;
        _semanticDocument = semanticDocument;
        _tracker = tracker;
        _declarativeInlines = declarativeInlines;
        _flattener = flattener;
    }

    internal void AddInlines(
        InlineContainerBox box,
        ContainerInline? inline,
        int inheritedAliasStart = -1,
        Inline? skipInline = null)
    {
        if (inline is null) return;
        var htmlState = new SafeHtmlInlineState(_context.Registry.SafeHtmlPolicy);
        AddInlines(
            box,
            inline,
            htmlState,
            inheritedAliasStart,
            Array.Empty<string>(),
            containingLinkUrl: null,
            containingLinkTitle: null,
            skipInline: skipInline);
        if (htmlState.BudgetExceeded)
            _tracker.AddHtmlBudgetNotice(box);
    }

    internal void AddExtensionContainerInlines(
        InlineContainerBox box, ContainerInline inlines,
        Func<Inline, bool>? skipFirstIf, int inheritedAliasStart)
    {
        _tracker.TrackBlock(box);
        _context.CancellationToken.ThrowIfCancellationRequested();
        Inline? first = inlines.FirstChild;
        Inline? skip = first is not null && skipFirstIf?.Invoke(first) == true ? first : null;
        AddInlines(box, inlines, inheritedAliasStart, skip);
    }

    internal void AddInlines(
        InlineContainerBox box,
        ContainerInline inline,
        SafeHtmlInlineState htmlState,
        int inheritedAliasStart,
        IReadOnlyList<string> inheritedStyleModifiers,
        string? containingLinkUrl,
        string? containingLinkTitle,
        Inline? skipInline = null)
    {
        foreach (var i in inline)
        {
            if (ReferenceEquals(i, skipInline))
                continue;

            _context.CancellationToken.ThrowIfCancellationRequested();
            if (!htmlState.TryAcceptNode(i))
                break;
            int aliasStart = _context.StyleAliasCount;
            using var inlineAttrs = _context.PushMarkdownAttributes(i);

            if (_semanticDocument is { } semanticDocument &&
                semanticDocument.TryGetInlineExtensionContent(i, out var declarativeFragment) &&
                declarativeFragment is not null &&
                _declarativeInlines.TryCreateDeclarativeInlineRuns(declarativeFragment, out var declarativeRuns))
            {
                int declarativeAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                IReadOnlyList<string> aliases = _context.CreateStyleAliasSnapshotFrom(declarativeAliasStart);
                _context.RegisterMarkdownAttributes(i, box.BlockIndex);
                foreach (InlineRun sourceRun in declarativeRuns)
                {
                    InlineRun declarativeRun = MarkdownInlineSyntax.ApplyContainingLink(
                        sourceRun,
                        containingLinkUrl,
                        containingLinkTitle) ?? sourceRun;
                    declarativeRun.StyleAliases = aliases;
                    _tracker.AddInlineRun(box, declarativeRun);
                }
                continue;
            }

            if (i is EmphasisInline emphasis)
            {
                string emphasisKey = MarkdownInlineSyntax.GetEmphasisElementKey(emphasis);
                if (_context.ThemeSnapshot.HasRelativeScriptFontSizeRules || MarkdownInlineSyntax.ContainsMarkdownLink(emphasis))
                {
                    _context.RegisterMarkdownAttributes(i, box.BlockIndex);
                    int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                    using SafeHtmlInlineState.ScriptStyleScope scriptScope =
                        htmlState.PushMarkdownScript(emphasisKey);
                    AddInlines(
                        box,
                        emphasis,
                        htmlState,
                        effectiveAliasStart,
                        MarkdownInlineSyntax.AppendStyleModifier(
                            inheritedStyleModifiers,
                            emphasisKey),
                        containingLinkUrl,
                        containingLinkTitle);
                    continue;
                }
            }

            InlineRun? run;
            if (i is HtmlInline html)
            {
                run = _tracker.TrackInlineImageRun(htmlState.Process(
                    html,
                    _context,
                    containingLinkUrl,
                    containingLinkTitle));
            }
            else if (i is LinkInline link &&
                     MarkdownInlineSyntax.TryGetOnlyHtmlImageChild(link, out HtmlInline? linkedHtmlImage) &&
                     htmlState.IsStandaloneImage(linkedHtmlImage, _context))
            {
                run = _tracker.TrackInlineImageRun(htmlState.Process(
                    linkedHtmlImage,
                    _context,
                    link.Url,
                    link.Title,
                    MarkdownInlineSyntax.GetLinkedHtmlImageSourceSpan(link, linkedHtmlImage, _context)));
            }
            else if (i is LinkInline mixedLink &&
                     !mixedLink.IsImage &&
                     ContainsRenderableLinkedInlineObject(mixedLink, htmlState, _context))
            {
                _context.RegisterMarkdownAttributes(i, box.BlockIndex);
                int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                AddInlines(
                    box,
                    mixedLink,
                    htmlState,
                    effectiveAliasStart,
                    inheritedStyleModifiers,
                    mixedLink.Url,
                    mixedLink.Title);
                continue;
            }
            else if (!string.IsNullOrWhiteSpace(containingLinkUrl) &&
                     i is LinkInline { IsImage: true } containedImage)
            {
                run = BuildImageRun(
                    containedImage,
                    containingLinkUrl,
                    containingLinkTitle,
                    containedImage.Span.Start,
                    containedImage.Span.Length);
            }
            else
            {
                int linkStyleAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                run = htmlState.Apply(BuildInline(i, box.BlockIndex, linkStyleAliasStart));
                run = MarkdownInlineSyntax.ApplyContainingLink(run, containingLinkUrl, containingLinkTitle);
            }
            if (run is not null)
            {
                run.StyleModifierKeys = MarkdownInlineSyntax.CombineStyleAliases(
                    run.StyleModifierKeys,
                    inheritedStyleModifiers);
                int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                run.StyleAliases = MarkdownInlineSyntax.CombineStyleAliases(
                    run.StyleAliases,
                    _context.CreateStyleAliasSnapshotFrom(effectiveAliasStart));
                _context.RegisterMarkdownAttributes(i, box.BlockIndex);
                _tracker.AddInlineRun(box, run);
            }
            else if (i is ContainerInline nested)
            {
                _context.RegisterMarkdownAttributes(i, box.BlockIndex);
                int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                AddInlines(
                    box,
                    nested,
                    htmlState,
                    effectiveAliasStart,
                    inheritedStyleModifiers,
                    containingLinkUrl,
                    containingLinkTitle);
            }
        }
    }

    private bool ContainsRenderableLinkedInlineObject(
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
            if (_semanticDocument is not null &&
                _semanticDocument.TryGetInlineExtensionContent(child, out MarkdownContentFragment? fragment) &&
                fragment is not null &&
                ContainsRenderableVectorScene(fragment.Items))
            {
                return true;
            }
            if (child is ContainerInline nested &&
                ContainsRenderableLinkedInlineObject(nested, htmlState, context))
            {
                return true;
            }
        }

        return false;

        static bool ContainsRenderableVectorScene(IReadOnlyList<MarkdownContent> content)
        {
            foreach (MarkdownContent item in content)
            {
                if (item.Kind == MarkdownContentKind.VectorScene && item.VectorScene is not null)
                    return true;
                if (item.Children.Count > 0 && ContainsRenderableVectorScene(item.Children))
                    return true;
            }

            return false;
        }
    }

    private InlineRun? BuildInline(
        Inline inline,
        int parentBlockIndex = -1,
        int inlineStyleAliasStart = -1)
    {
        switch (inline)
        {
            case LiteralInline lit:
            {
                var span = new SourceSpan(lit.Span.Start, lit.Span.Length);
                string? sourceText = lit.Content.Text;
                if (sourceText is not null &&
                    ReferenceEquals(sourceText, _context.SourceMap.SourceText) &&
                    span.Length > 0 &&
                    span.Start >= 0 &&
                    span.Start <= sourceText.Length - span.Length &&
                    lit.Content.Start == span.Start &&
                    lit.Content.Length == span.Length)
                {
                    return new SourceSliceTextRun(sourceText, span.Start, span.Length)
                    {
                        SourceSpan = span,
                    };
                }

                return new TextRun(lit.Content.ToString())
                {
                    SourceSpan = span,
                };
            }
            case CodeInline ci:
                return new CodeInlineRun(ci.Content)
                {
                    SourceSpan = new SourceSpan(ci.Span.Start, ci.Span.Length)
                };
            case EmphasisInline emph:
                return BuildEmphasis(emph);
            case LinkInline link:
                return BuildLink(link, inlineStyleAliasStart);
            case LineBreakInline lineBreak:
                return new LineBreakRun(lineBreak.IsHard) { SourceSpan = new SourceSpan(inline.Span.Start, inline.Span.Length) };
            case AutolinkInline al:
                return new LinkRun(al.Url, al.Url) { SourceSpan = new SourceSpan(al.Span.Start, al.Span.Length) };
            case HtmlEntityInline entity:
                return new TextRun(entity.Transcoded.ToString())
                {
                    SourceSpan = new SourceSpan(entity.Span.Start, entity.Span.Length)
                };
            case AbbreviationInline abbreviation:
                return new AbbreviationRun(
                    abbreviation.Abbreviation?.Label ?? string.Empty,
                    abbreviation.Abbreviation?.Text.ToString() ?? string.Empty)
                {
                    SourceSpan = new SourceSpan(abbreviation.Span.Start, abbreviation.Span.Length)
                };
            case FootnoteLink fl when !fl.IsBackLink:
            {
                // Render footnote forward-references as clickable superscript links.
                // URL uses the internal fragment scheme "#footnote-def-{order}" which
                // MarkdownRendererControl intercepts to scroll to the definition.
                // Use fl.Footnote.Order (the footnote's 1-based sequence number), NOT
                // fl.Index (which is a global sequential counter across all citations
                // of all footnotes and differs from Order when a footnote is cited
                // more than once).
                int order = fl.Footnote is { } footnote
                    ? _context.GetOrCreateFootnoteOrder(footnote, fl.Index)
                    : Math.Max(1, fl.Index);
                var run = new LinkRun(MarkdownInlineSyntax.ToSuperscript(order), $"#footnote-def-{order}")
                {
                    SourceSpan = new SourceSpan(fl.Span.Start, fl.Span.Length),
                    IsSuperscript = true,
                };
                // Record the containing paragraph as the "ref" block so the
                // footnote definition's ↩ link can scroll back to the citation.
                if (parentBlockIndex >= 0) _context.RegisterFootnoteRef(order, parentBlockIndex);
                return run;
            }
        }
        return null;
    }

    private InlineRun BuildEmphasis(EmphasisInline emph)
    {
        var sb = new System.Text.StringBuilder();
        _flattener.FlattenContainer(emph, sb);
        var span = new SourceSpan(emph.Span.Start, emph.Span.Length);
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

    private InlineRun BuildLink(LinkInline link, int inlineStyleAliasStart)
    {
        if (link.IsImage)
        {
            return BuildImageRun(link, linkUrl: null, linkTitle: null, link.Span.Start, link.Span.Length);
        }

        if (MarkdownInlineSyntax.TryGetOnlyImageChild(link, out var imageLink))
        {
            return BuildImageRun(imageLink, link.Url, link.Title, link.Span.Start, link.Span.Length);
        }

        var sb = new System.Text.StringBuilder();
        LinkInlineStyleMapBuilder? inlineStyles = null;
        _flattener.FlattenContainer(
            link,
            sb,
            ref inlineStyles,
            path: null,
            inlineStyleAliasStart);
        return new LinkRun(
            sb.ToString(),
            link.Url ?? string.Empty,
            link.Title,
            inlineStyles?.Build())
        {
            SourceSpan = new SourceSpan(link.Span.Start, link.Span.Length)
        };
    }

    private InlineImageRun BuildImageRun(
        LinkInline imageLink,
        string? linkUrl,
        string? linkTitle,
        int sourceStart,
        int sourceLength)
    {
        var alt = new System.Text.StringBuilder();
        _flattener.FlattenContainer(imageLink, alt);
        // Preserve explicitly empty alt text. The semantic layer omits an
        // unlinked empty-alt image instead of inventing an accessible name.
        string altText = alt.ToString();
        SafeHtmlLength? requestedWidth = null;
        SafeHtmlLength? requestedHeight = null;
        if (imageLink is SizedImageLinkInline sizedImage)
        {
            requestedWidth = sizedImage.RequestedWidth;
            requestedHeight = sizedImage.RequestedHeight;
        }

        return _tracker.TrackInlineImageRun(new InlineImageRun(
            _context,
            altText,
            imageLink.Url ?? string.Empty,
            imageLink.Title,
            linkUrl,
            linkTitle,
            requestedWidth,
            requestedHeight)
        {
            SourceSpan = new SourceSpan(sourceStart, sourceLength)
        });
    }
}
