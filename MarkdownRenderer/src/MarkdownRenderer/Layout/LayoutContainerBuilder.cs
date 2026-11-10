using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Markdig.Syntax;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Builds quote, list and generic container blocks and populates their
/// children through the block dispatcher.
/// </summary>
internal sealed class LayoutContainerBuilder
{
    private const string ListContainerStyleRole = MarkdownElementKeys.List;

    private readonly MarkdownLayoutContext _context;
    private readonly MarkdownRenderer.Document.MarkdownDocument? _semanticDocument;
    private readonly IReadOnlySet<HostedElementFallbackKey> _hostedElementFallbacks;
    private readonly ILayoutBlockDispatcher _blockDispatcher;
    private readonly LayoutConstructionTracker _tracker;
    private readonly DeclarativeBlockBuilder _declarativeBlocks;

    internal LayoutContainerBuilder(
        MarkdownLayoutContext context,
        MarkdownRenderer.Document.MarkdownDocument? semanticDocument,
        IReadOnlySet<HostedElementFallbackKey> hostedElementFallbacks,
        ILayoutBlockDispatcher blockDispatcher,
        LayoutConstructionTracker tracker,
        DeclarativeBlockBuilder declarativeBlocks)
    {
        _context = context;
        _semanticDocument = semanticDocument;
        _hostedElementFallbacks = hostedElementFallbacks;
        _blockDispatcher = blockDispatcher;
        _tracker = tracker;
        _declarativeBlocks = declarativeBlocks;
    }

    internal StackBox BuildQuote(QuoteBlock qb)
    {
        var style = _context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.Quote,
            _context.CreateStyleContextSnapshot(),
            _context.CreateStyleAliasSnapshot());
        var stack = new StackBox
        {
            ContentPadding = style.Padding,
            AccentBar = style.AccentBar,
            AccentBarThickness = style.AccentBarThickness,
            AccentBarAffectsContentLayout = style.AccentBarAffectsContentLayout,
            Background = style.Background,
            BorderBrush = style.BorderBrush,
            BorderThickness = style.BorderThickness,
            CornerRadius = style.CornerRadius,
            Margin = style.Margin,
            FlowDirection = _context.FlowDirection,
        };
        _tracker.TrackBlock(stack);
        stack.BlockIndex = _context.NextBlockIndex();
        using var quoteScoue = _context.Styles.PushContext(MarkdownElementKeys.Quote);
        PopulateBlockChildren(stack, qb);
        CollapseTrailingQuoteParagraphMargin(stack);
        return stack;
    }

    private void CollapseTrailingQuoteParagraphMargin(StackBox quote)
    {
        // CSS collapses a last child's bottom margin with its parent's bottom
        // margin only when no bottom padding or border separates them. Keep the
        // larger positive margin, and limit this to stylesheets that opt into
        // margin collapse so authored/default additive layouts remain intact.
        if ((_context.ThemeSnapshot.LayoutPolicy & 1) == 0 ||
            quote.ContentPadding.Bottom > 0 ||
            quote.BorderThickness > 0 ||
            quote.Children.Count == 0)
        {
            return;
        }

        BlockBox trailingBlock = quote.Children[quote.Children.Count - 1];
        Thickness paragraphMargin;
        switch (trailingBlock)
        {
            case InlineContainerBox trailingParagraph:
                paragraphMargin = trailingParagraph.Margin;
                break;
            case ImageBox trailingImage:
                // Markdown image-only paragraphs are promoted to ImageBox but
                // still carry the paragraph's margin and line-box geometry.
                paragraphMargin = trailingImage.Margin;
                break;
            default:
                return;
        }

        double collapsedMargin = StackBox.GetPositiveCollapsedMargin(
            paragraphMargin.Bottom,
            quote.Margin.Bottom);
        if (collapsedMargin <= 0)
            return;

        quote.RememberCollapsedTrailingChildMargin(trailingBlock, paragraphMargin);

        var collapsedParagraphMargin = new Thickness(
            paragraphMargin.Left,
            paragraphMargin.Top,
            paragraphMargin.Right,
            paragraphMargin.Bottom - collapsedMargin);
        if (trailingBlock is InlineContainerBox inlineParagraph)
        {
            inlineParagraph.Margin = collapsedParagraphMargin;
            inlineParagraph.UseContainerMargin = false;
        }
        else if (trailingBlock is ImageBox imageParagraph)
        {
            imageParagraph.Margin = collapsedParagraphMargin;
        }
    }

    internal StackBox BuildList(ListBlock list)
    {
        using var listScope = _context.Styles.PushListDepth();
        Thickness listMargin = _context.ThemeSnapshot.GetStyle(
            ListContainerStyleRole,
            _context.CreateStyleContextSnapshot(),
            _context.CreateStyleAliasSnapshot()).Margin;
        var stack = new StackBox
        {
            FlowDirection = _context.FlowDirection,
            Margin = listMargin,
            InterChildSpacing = list.IsLoose ? 0 : GetTightListSiblingSpacing(),
        };
        _tracker.TrackBlock(stack);
        stack.BlockIndex = _context.NextBlockIndex();
        // Honour the ordered-list start number from the source (e.g. `5.`).
        // Markdig stores this as a string on ListBlock.OrderedStart.
        int index = 1;
        if (list.IsOrdered && !string.IsNullOrEmpty(list.OrderedStart)
            && int.TryParse(list.OrderedStart, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            index = parsed;
        }
        foreach (var item in list)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            if (item is not ListItemBlock li) continue;

            BlockBox? itemBox = null;
            using (var itemAttrs = _context.PushMarkdownAttributes(li))
            {
                if (_semanticDocument is { } document &&
                    DeclarativeBlockSelection.TryGetContent(
                        document,
                        li,
                        _hostedElementFallbacks,
                        out var extensionFragment))
                {
                    itemBox = _declarativeBlocks.TryBuildDeclarativeBlock(extensionFragment!);
                }

                if (itemBox is null &&
                    _context.Registry.TryGetRenderer(typeof(ListItemBlock), out var itemRenderer) &&
                    itemRenderer is not null)
                    itemBox = itemRenderer.BuildBlock(li, _context);

                itemBox ??= BuildDefaultListItem(li, list.IsOrdered, index, isTightList: !list.IsLoose);

                _tracker.TrackBlock(itemBox);
                if (itemBox.BlockIndex == 0) itemBox.BlockIndex = _context.NextBlockIndex();
                _context.RegisterMarkdownAttributes(li, itemBox.BlockIndex);
                _tracker.AddStackChild(stack, itemBox);
            }
            index++;
        }
        return stack;
    }

    private ListItemBox BuildDefaultListItem(
        ListItemBlock li,
        bool isOrdered,
        int index,
        bool isTightList)
    {
        var listStyle = _context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.ListMarker,
            _context.CreateStyleContextSnapshot(),
            _context.CreateStyleAliasSnapshot());
        float markerWidth = Math.Max(1f, listStyle.ListIndent + Math.Max(0, _context.Styles.ListDepth - 1) * listStyle.NestedListIndent);

        // Marker gutter — fixed width; center the unordered bullet without
        // changing the content column or ordered-marker alignment.
        var marker = new InlineContainerBox(_context, MarkdownElementKeys.ListMarker);
        _tracker.TrackBlock(marker);
        marker.BlockIndex = _context.NextBlockIndex();
        marker.TextAlignment = isOrdered
            ? CanvasHorizontalAlignment.Left
            : CanvasHorizontalAlignment.Center;
        string markerText = isOrdered ? $"{index}." : "•";
        marker.Add(new TextRun(markerText)
        {
            ElementKey = MarkdownElementKeys.ListMarker,
            SourceSpan = new SourceSpan(li.Span.Start, 0)
        });

        // Content area — all child blocks of the list item.
        var content = new StackBox
        {
            FlowDirection = _context.FlowDirection,
        };
        _tracker.TrackBlock(content);
        content.BlockIndex = _context.NextBlockIndex();
        PopulateBlockChildren(content, li, suppressDirectParagraphMargins: isTightList);

        var itemBox = new ListItemBox(marker, content, markerWidth)
        {
            FlowDirection = _context.FlowDirection,
        };
        _tracker.TrackBlock(itemBox);
        _tracker.AdoptBlock(marker);
        _tracker.AdoptBlock(content);
        return itemBox;
    }

    internal StackBox BuildGenericContainer(ContainerBlock cb)
    {
        var stack = new StackBox
        {
            FlowDirection = _context.FlowDirection,
        };
        _tracker.TrackBlock(stack);
        stack.BlockIndex = _context.NextBlockIndex();
        PopulateBlockChildren(stack, cb);
        return stack;
    }

    private float GetTightListSiblingSpacing()
    {
        ElementStyle bodyStyle = _context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.Body,
            _context.CreateStyleContextSnapshot(),
            _context.CreateStyleAliasSnapshot());
        float fontSize = bodyStyle.FontSize;
        return float.IsFinite(fontSize) && fontSize > 0
            ? fontSize * 0.25f
            : 0;
    }

    private void PopulateBlockChildren(
        StackBox destination,
        ContainerBlock container,
        bool suppressDirectParagraphMargins = false)
    {
        SafeHtmlBlockScopeTracker? htmlScopes = _context.Registry.SafeHtmlPolicy is null
            ? null
            : _context.SafeHtmlBlock.NestedScopes ??
              new SafeHtmlBlockScopeTracker(_context.Registry.SafeHtmlPolicy.Limits);
        bool htmlBudgetNoticeAdded = htmlScopes?.BudgetExceeded == true;
        foreach (Block child in container)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            bool suppressedBeforeBlock = htmlScopes?.IsContentSuppressed == true;
            SafeHtmlAlignment alignmentAtBlockEntry = htmlScopes?.CurrentAlignment ?? SafeHtmlAlignment.Inherit;
            int? inlineFlowScopeIdAtBlockEntry = htmlScopes?.CurrentInlineFlowScopeId;
            bool scopeOnly = child is HtmlBlock htmlBlock && htmlScopes?.Process(
                    htmlBlock.Lines.ToString(),
                    htmlBlock.Span.Start,
                    _context.DisclosureStates,
                    _context.CancellationToken) == true;
            int? inlineFlowScopeIdForBlock = LayoutBlockBuilder.ResolveInlineFlowScopeForBlock(
                inlineFlowScopeIdAtBlockEntry,
                htmlScopes?.CurrentInlineFlowScopeId);
            bool inlineFlowContinuesPrevious = inlineFlowScopeIdForBlock is int scopeId &&
                destination.Children.Count > 0 &&
                destination.Children[^1].SafeHtmlInlineFlowScopeId == scopeId;
            if (suppressedBeforeBlock && child is not HtmlBlock)
            {
                htmlScopes?.ObserveBlockTags(
                    child,
                    _context.DisclosureStates,
                    _context.CancellationToken);
            }

            if (htmlScopes?.BudgetExceeded == true)
            {
                if (!htmlBudgetNoticeAdded)
                {
                    AddHtmlBudgetNotice(destination);
                    htmlBudgetNoticeAdded = true;
                }

                if (child is HtmlBlock || suppressedBeforeBlock)
                    continue;
            }

            if (scopeOnly || suppressedBeforeBlock)
                continue;

            BlockBox? box = BuildChildBlock(
                child,
                htmlScopes,
                alignmentAtBlockEntry,
                inlineFlowScopeIdForBlock,
                inlineFlowContinuesPrevious,
                suppressDirectParagraphMargins);
            if (box is null)
                continue;

            _tracker.AddStackChild(destination, box);

            if (!suppressedBeforeBlock && child is not HtmlBlock)
            {
                htmlScopes?.ObserveBlockTags(
                    child,
                    _context.DisclosureStates,
                    _context.CancellationToken);
                if (htmlScopes?.BudgetExceeded == true && !htmlBudgetNoticeAdded)
                {
                    AddHtmlBudgetNotice(destination);
                    htmlBudgetNoticeAdded = true;
                }
            }
        }
    }

    // Builds one child under the safe-HTML context captured at its entry and
    // applies paragraph margin, alignment and inline-flow adjustments.
    private BlockBox? BuildChildBlock(
        Block child,
        SafeHtmlBlockScopeTracker? htmlScopes,
        SafeHtmlAlignment alignmentAtBlockEntry,
        int? inlineFlowScopeIdForBlock,
        bool inlineFlowContinuesPrevious,
        bool suppressDirectParagraphMargins)
    {
        SafeHtmlBlockScopeTracker? nestedScopes = child is ContainerBlock or HtmlBlock
            ? htmlScopes?.CloneForNestedContent()
            : null;
        BlockBox? box;
        using (_context.SafeHtmlBlock.Push(
                   alignmentAtBlockEntry,
                   nestedScopes,
                   inlineFlowScopeIdForBlock,
                   inlineFlowContinuesPrevious))
            box = _blockDispatcher.BuildBlock(child);
        if (box is null)
            return null;

        if (suppressDirectParagraphMargins && child is ParagraphBlock)
        {
            Thickness margin = box.Margin;
            box.Margin = new Thickness(margin.Left, 0, margin.Right, 0);
            if (box is InlineContainerBox paragraphBox)
                paragraphBox.UseContainerMargin = false;
        }

        if (htmlScopes is not null && child is not HtmlBlock)
            LayoutBlockBuilder.ApplyHtmlAlignment(box, alignmentAtBlockEntry);

        if (child is ParagraphBlock paragraph &&
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

    private void AddHtmlBudgetNotice(StackBox destination)
    {
        var notice = new InlineContainerBox(_context, MarkdownElementKeys.Body)
        {
            BlockIndex = _context.NextBlockIndex(),
        };
        _tracker.TrackBlock(notice);
        _tracker.AddHtmlBudgetNotice(notice);
        _tracker.AddStackChild(destination, notice);
    }
}
