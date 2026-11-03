using System;
using System.Collections.Generic;
using System.Threading;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Builds the top-level block sequence and dispatches each Markdig block to
/// its paragraph, heading, code, container, declarative or extension builder.
/// </summary>
internal sealed class LayoutBlockBuilder
{
    private readonly MarkdownLayoutContext _context;
    private readonly IMarkdownEmbedFactory? _embedFactory;
    private readonly MarkdownRenderer.Document.MarkdownDocument? _semanticDocument;
    private readonly IReadOnlySet<HostedElementFallbackKey> _hostedElementFallbacks;
    private readonly LayoutConstructionTracker _tracker;
    private readonly LayoutContainerBuilder _containers;
    private readonly DeclarativeBlockBuilder _declarativeBlocks;
    private readonly CodeBlockLayoutBuilder _codeBlocks;
    private readonly InlineRunBuilder _inlines;
    private readonly InlineContainerFlattener _flattener;

    internal LayoutBlockBuilder(
        MarkdownLayoutContext context,
        IMarkdownEmbedFactory? embedFactory,
        MarkdownRenderer.Document.MarkdownDocument? semanticDocument,
        IReadOnlySet<HostedElementFallbackKey> hostedElementFallbacks,
        LayoutConstructionTracker tracker,
        LayoutContainerBuilder containers,
        DeclarativeBlockBuilder declarativeBlocks,
        CodeBlockLayoutBuilder codeBlocks,
        InlineRunBuilder inlines,
        InlineContainerFlattener flattener)
    {
        _context = context;
        _embedFactory = embedFactory;
        _semanticDocument = semanticDocument;
        _hostedElementFallbacks = hostedElementFallbacks;
        _tracker = tracker;
        _containers = containers;
        _declarativeBlocks = declarativeBlocks;
        _codeBlocks = codeBlocks;
        _inlines = inlines;
        _flattener = flattener;
    }

    internal List<BlockBox> BuildBlocks(MarkdownDocument document, CancellationToken cancellationToken)
    {
        using var childBlockScope = _context.PushChildBlockBuilder(BuildBlock);
        using var childInlineScope = _context.PushChildInlineBuilder(_inlines.AddExtensionContainerInlines);
        var blocks = new List<BlockBox>();
        SafeHtmlBlockScopeTracker? htmlScopes = _context.Registry.SafeHtmlPolicy is null
            ? null
            : new SafeHtmlBlockScopeTracker(_context.Registry.SafeHtmlPolicy.Limits);
        bool htmlBudgetNoticeAdded = false;
        foreach (var b in document)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool suppressedBeforeBlock = htmlScopes?.IsContentSuppressed == true;
            SafeHtmlAlignment alignmentAtBlockEntry = htmlScopes?.CurrentAlignment ?? SafeHtmlAlignment.Inherit;
            int? inlineFlowScopeIdAtBlockEntry = htmlScopes?.CurrentInlineFlowScopeId;
            bool scopeOnly = b is HtmlBlock htmlBlock && htmlScopes?.Process(
                    htmlBlock.Lines.ToString(),
                    htmlBlock.Span.Start,
                    _context.DisclosureStates,
                    cancellationToken) == true;
            int? inlineFlowScopeIdForBlock = htmlScopes?.CurrentInlineFlowScopeId;
            if (inlineFlowScopeIdAtBlockEntry is int entryScopeId &&
                inlineFlowScopeIdForBlock != entryScopeId)
            {
                // Fail closed when this HtmlBlock exits the flow that was open
                // at entry (for example `</div><br>outside`). A newly opened
                // scope may still qualify if the renderer confirms the block is
                // exactly that scope's inline-only content.
                inlineFlowScopeIdForBlock = null;
            }
            bool inlineFlowContinuesPrevious = inlineFlowScopeIdForBlock is int scopeId &&
                blocks.Count > 0 &&
                blocks[^1].SafeHtmlInlineFlowScopeId == scopeId;
            if (suppressedBeforeBlock && b is not HtmlBlock)
            {
                htmlScopes?.ObserveBlockTags(
                    b,
                    _context.DisclosureStates,
                    cancellationToken);
            }

            if (htmlScopes?.BudgetExceeded == true)
            {
                if (!htmlBudgetNoticeAdded)
                {
                    blocks.Add(CreateHtmlBudgetNotice());
                    htmlBudgetNoticeAdded = true;
                }
                if (b is HtmlBlock || suppressedBeforeBlock)
                    continue;
            }
            if (scopeOnly)
            {
                continue;
            }

            if (suppressedBeforeBlock)
            {
                continue;
            }

            if (BuildTopLevelBlock(
                    b,
                    htmlScopes,
                    alignmentAtBlockEntry,
                    inlineFlowScopeIdForBlock,
                    inlineFlowContinuesPrevious) is { } box)
                blocks.Add(box);

            if (!suppressedBeforeBlock && b is not HtmlBlock)
            {
                htmlScopes?.ObserveBlockTags(
                    b,
                    _context.DisclosureStates,
                    cancellationToken);
                if (htmlScopes?.BudgetExceeded == true && !htmlBudgetNoticeAdded)
                {
                    blocks.Add(CreateHtmlBudgetNotice());
                    htmlBudgetNoticeAdded = true;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        ApplyTopLevelMarginPolicy(blocks);
        return blocks;
    }

    // Builds one top-level block under the safe-HTML context captured at its
    // entry. Returns null when the block produced nothing to publish.
    private BlockBox? BuildTopLevelBlock(
        Block b,
        SafeHtmlBlockScopeTracker? htmlScopes,
        SafeHtmlAlignment alignmentAtBlockEntry,
        int? inlineFlowScopeIdForBlock,
        bool inlineFlowContinuesPrevious)
    {
        BlockBox? box;
        SafeHtmlBlockScopeTracker? nestedScopes = b is ContainerBlock or HtmlBlock
            ? htmlScopes?.CloneForNestedContent()
            : null;
        using (_context.PushSafeHtmlBlockContext(
                   alignmentAtBlockEntry,
                   nestedScopes,
                   inlineFlowScopeIdForBlock,
                   inlineFlowContinuesPrevious))
            box = BuildBlock(b);
        if (box is null)
            return null;

        // The safe renderer has already claimed this HtmlBlock, so an
        // empty renderer-owned root must not reintroduce vertical gap
        // for comments/declarations. It cannot fall back to literal
        // core rendering because BuildBlock has already dispatched it.
        if (b is HtmlBlock && box is StackBox { Children.Count: 0 })
        {
            _tracker.DiscardUnpublishedBlock(box);
            return null;
        }

        if (htmlScopes is not null &&
            b is not HtmlBlock)
            ApplyHtmlAlignment(box, alignmentAtBlockEntry);

        if (b is ParagraphBlock paragraph &&
            box is InlineContainerBox inlineParagraph &&
            inlineFlowScopeIdForBlock is int inlineFlowScopeId &&
            SafeHtmlFlowParagraphs.IsBadgeOnlyHtmlFlowParagraph(paragraph))
        {
            inlineParagraph.SafeHtmlInlineFlowScopeId = inlineFlowScopeId;
            inlineParagraph.UseContainerMargin = false;
            inlineParagraph.Margin = default;
        }

        return box;
    }

    private InlineContainerBox CreateHtmlBudgetNotice()
    {
        var notice = new InlineContainerBox(_context, MarkdownElementKeys.Body)
        {
            BlockIndex = _context.NextBlockIndex(),
        };
        _tracker.TrackBlock(notice);
        _tracker.AddHtmlBudgetNotice(notice);
        return notice;
    }

    private void ApplyTopLevelMarginPolicy(List<BlockBox> blocks)
    {
        byte layoutPolicy = _context.ThemeSnapshot.LayoutPolicy;
        if ((layoutPolicy & 4) != 0)
            TopLevelBlockMarginCollapse.ResetLeadingTopMargin(blocks);
        if ((layoutPolicy & 1) != 0)
            TopLevelBlockMarginCollapse.Apply(blocks);
        if ((layoutPolicy & 2) != 0)
            TopLevelBlockMarginCollapse.ResetTrailingBottomMargin(blocks);
    }

    internal static void ApplyHtmlAlignment(BlockBox box, SafeHtmlAlignment alignment)
    {
        if (box is StackBox { HasResolvedSafeHtmlAlignment: true })
            return;

        if (alignment == SafeHtmlAlignment.Inherit)
        {
            return;
        }

        var canvasAlignment = alignment switch
        {
            SafeHtmlAlignment.Center => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
            SafeHtmlAlignment.Right => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Right,
            _ => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left,
        };

        switch (box)
        {
            case InlineContainerBox inline:
                inline.TextAlignment = canvasAlignment;
                break;
            case ImageBox image:
                image.ContentAlignment = canvasAlignment;
                break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    ApplyHtmlAlignment(child, alignment);
                }
                break;
        }
    }

    internal BlockBox? BuildBlock(Block block)
    {
        _context.CancellationToken.ThrowIfCancellationRequested();
        using var attrScope = _context.PushMarkdownAttributes(block);
        BlockBox? box = null;

        if (_semanticDocument is { } document &&
            DeclarativeBlockSelection.TryGetContent(
                document,
                block,
                _hostedElementFallbacks,
                out var extensionFragment))
        {
            box = _declarativeBlocks.TryBuildDeclarativeBlock(extensionFragment!);
        }

        if (box is null && _embedFactory is { } ef)
        {
            _context.ThrowIfEmbedLayoutCallbackIsOnUiThread(nameof(IMarkdownEmbedFactory.CanCreate));
            if (!ef.CanCreate(block))
                goto SkipEmbedFactory;

            var eb = new EmbedBox(block, ef, _context);
            _tracker.TrackBlock(eb);
            eb.BlockIndex = _context.NextBlockIndex();
            // Register the source span so Ctrl+C across an embed copies the
            // original markdown that produced it.
            if (block.Span.Length > 0)
            {
                var span = new MarkdownRenderer.SourceSpan(block.Span.Start, block.Span.Length);
                _context.SourceMap.Add(eb.BlockIndex, 0, 1, span);
            }
            box = eb;
        }

    SkipEmbedFactory:

        if (box is null && _context.Registry.TryGetRenderer(block.GetType(), out var renderer) && renderer is not null)
        {
            var custom = renderer.BuildBlock(block, _context);
            if (custom is not null)
            {
                if (custom.BlockIndex == 0) custom.BlockIndex = _context.NextBlockIndex();
                box = custom;
            }
        }

        box ??= block switch
        {
            HeadingBlock h => BuildHeading(h),
            ParagraphBlock p => BuildParagraphOrImage(p),
            FencedCodeBlock fc => _codeBlocks.BuildCodeBlock(fc, fc.Lines.ToString()),
            CodeBlock cb => _codeBlocks.BuildCodeBlock(cb, cb.Lines.ToString()),
            QuoteBlock qb => _containers.BuildQuote(qb),
            ListBlock lb => _containers.BuildList(lb),
            ThematicBreakBlock => MakeThematicBreak(),
            HtmlBlock html => BuildLiteralHtmlBlock(html),
            ContainerBlock cb => _containers.BuildGenericContainer(cb),
            _ => null
        };

        if (box is not null)
        {
            _tracker.TrackBlock(box);
            _context.RegisterMarkdownAttributes(block, box.BlockIndex);
        }

        return box;
    }

    private InlineContainerBox BuildLiteralHtmlBlock(HtmlBlock block)
    {
        var box = new InlineContainerBox(_context, MarkdownElementKeys.Body)
        {
            BlockIndex = _context.NextBlockIndex(),
        };
        _tracker.TrackBlock(box);
        string source = block.Lines.ToString();
        box.Add(new TextRun(source)
        {
            SourceSpan = new SourceSpan(block.Span.Start, Math.Max(0, block.Span.Length)),
        });
        return box;
    }

    private BlockBox BuildParagraphOrImage(ParagraphBlock p)
    {
        // If the paragraph contains only a single image link (optionally wrapped
        // in a single ContainerInline), promote to an ImageBox.
        var inline = p.Inline;
        if (inline is not null)
        {
            LinkInline? onlyImage = null;
            int count = 0;
            foreach (var node in inline)
            {
                count++;
                if (count > 1) { onlyImage = null; break; }
                if (node is LinkInline li && li.IsImage) onlyImage = li;
                else { onlyImage = null; break; }
            }
            if (onlyImage is not null)
            {
                string url = onlyImage.Url ?? string.Empty;
                var altSb = new System.Text.StringBuilder();
                _flattener.FlattenContainer(onlyImage, altSb);
                string alt = altSb.ToString();
                ElementStyle paragraphStyle = _context.ThemeSnapshot.GetStyle(
                    MarkdownElementKeys.Body,
                    _context.CreateStyleContextSnapshot(),
                    _context.CreateStyleAliasSnapshot());
                var img = new ImageBox(_context, url, alt)
                {
                    // This block represents the entire image-only paragraph, not
                    // only its image element. Reuse paragraph-flow margins while
                    // keeping the image atomic for painting and accessibility.
                    Margin = paragraphStyle.Margin,
                    MinimumParagraphLineHeight = paragraphStyle.FontSize * paragraphStyle.LineHeightMultiplier,
                };
                _tracker.TrackBlock(img);
                img.BlockIndex = _context.NextBlockIndex();
                // Register the source span so Ctrl+C copies the original ![alt](url).
                var span = new MarkdownRenderer.SourceSpan(p.Span.Start, p.Span.Length);
                _context.SourceMap.Add(img.BlockIndex, 0, 1, span);
                return img;
            }
        }
        return BuildParagraph(p);
    }

    private BlockBox MakeThematicBreak()
    {
        var box = new ThematicBreakBox(_context);
        _tracker.TrackBlock(box);
        box.BlockIndex = _context.NextBlockIndex();
        return box;
    }

    private InlineContainerBox BuildHeading(HeadingBlock h)
    {
        string key = h.Level switch
        {
            1 => MarkdownElementKeys.Heading1,
            2 => MarkdownElementKeys.Heading2,
            3 => MarkdownElementKeys.Heading3,
            4 => MarkdownElementKeys.Heading4,
            5 => MarkdownElementKeys.Heading5,
            _ => MarkdownElementKeys.Heading6
        };
        var box = new InlineContainerBox(_context, key);
        _tracker.TrackBlock(box);
        box.BlockIndex = _context.NextBlockIndex();
        _inlines.AddInlines(box, h.Inline);
        if (h.Span.Start >= 0 && h.Span.Length > 0)
            _context.SourceMap.AddSourceAffixesToBlock(box.BlockIndex, h.Span.Start, h.Span.Start + h.Span.Length);
        return box;
    }

    private InlineContainerBox BuildParagraph(ParagraphBlock p)
    {
        var box = new InlineContainerBox(_context, MarkdownElementKeys.Body);
        _tracker.TrackBlock(box);
        box.BlockIndex = _context.NextBlockIndex();
        Inline? leadingFlowBreak = _context.SafeHtmlInlineFlowContinuesPrevious
            ? SafeHtmlFlowParagraphs.FindLeadingHtmlBreak(p.Inline)
            : null;
        _inlines.AddInlines(box, p.Inline, skipInline: leadingFlowBreak);
        return box;
    }
}
