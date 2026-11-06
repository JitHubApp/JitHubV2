using System;
using System.Collections.Generic;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Recursive private clone construction for one
/// <see cref="PrivateRasterPlacementTree"/> clone call. The node and
/// style-key budgets are shared by the whole walk, and every partially built
/// owner is discarded into the target context if a descendant fails.
/// </summary>
internal sealed class PrivateRasterPlacementCloner
{
    private readonly MarkdownLayoutContext _sourceContext;
    private readonly MarkdownLayoutContext _targetContext;
    private readonly List<RasterPlacementSourceFence> _fences;
    private readonly List<CodeBlockPlacementSourceFence>? _codeSources;
    private readonly bool _allowUnmeasuredTextOnly;
    private readonly bool _allowIdleImagePlacements;
    private readonly bool _allowMeasuredVectorPlacement;
    private int _clonedNodes;
    private long _styleKeyCopyEntries;

    internal PrivateRasterPlacementCloner(
        MarkdownLayoutContext sourceContext,
        MarkdownLayoutContext targetContext,
        List<RasterPlacementSourceFence> fences,
        List<CodeBlockPlacementSourceFence>? codeSources,
        bool allowUnmeasuredTextOnly,
        bool allowIdleImagePlacements,
        bool allowMeasuredVectorPlacement)
    {
        _sourceContext = sourceContext;
        _targetContext = targetContext;
        _fences = fences;
        _codeSources = codeSources;
        _allowUnmeasuredTextOnly = allowUnmeasuredTextOnly;
        _allowIdleImagePlacements = allowIdleImagePlacements;
        _allowMeasuredVectorPlacement = allowMeasuredVectorPlacement;
    }

    internal bool TryCloneNode(BlockBox source, int depth, out BlockBox? clone)
    {
        _targetContext.CancellationToken.ThrowIfCancellationRequested();
        clone = null;
        if (depth > PrivateRasterPlacementTree.MaximumDepth ||
            _clonedNodes >= PrivateRasterPlacementTree.MaximumNodes || source.BlockIndex < 0)
            return false;
        _clonedNodes++;
        switch (source)
        {
            case ImageBox image when ReferenceEquals(image.Context, _sourceContext):
                return TryCloneImage(image, out clone);

            case InlineContainerBox inline when PrivateRasterPlacementTree.CaptureState.IsSupportedInlineElement(inline.ElementKey) &&
                                                inline.SafeHtmlInlineFlowScopeId is null &&
                                                ReferenceEquals(inline.Context, _sourceContext):
                return TryCloneInline(inline, out clone);

            case CodeBlockBox code when _codeSources is not null &&
                                        code.SafeHtmlInlineFlowScopeId is null &&
                                        ReferenceEquals(code.Context, _sourceContext):
                return TryCloneCode(code, _codeSources, depth, out clone);

            case StackBox stack when stack.GetType() == typeof(StackBox) &&
                                     stack.IsPrivateRasterPlacementCloneEligible &&
                                     stack.SafeHtmlInlineFlowScopeId is null:
                return TryCloneStack(stack, depth, out clone);

            case ListItemBox listItem when listItem.GetType() == typeof(ListItemBox) &&
                                           listItem.SafeHtmlInlineFlowScopeId is null:
                return TryCloneListItem(listItem, depth, out clone);

            case TableBox table when table.GetType() == typeof(TableBox) &&
                                     table.SafeHtmlInlineFlowScopeId is null &&
                                     ReferenceEquals(table.Context, _sourceContext):
                return TryCloneTable(table, depth, out clone);

            default:
                return false;
        }
    }

    private bool TryCloneImage(ImageBox image, out BlockBox? clone)
    {
        clone = null;
        ImageBox? privateImage = null;
        try
        {
            SharedCanvasBitmapCache.Lease? expectedSourceLease;
            CompletedSvgPlacementMetadataFence? svgMetadataFence;
            bool isIdle = false;
            int idleGeneration = 0;
            bool created = _allowIdleImagePlacements
                ? image.TryCreatePrivateImagePlacementClone(_targetContext, out privateImage,
                    out expectedSourceLease, out svgMetadataFence, out isIdle, out idleGeneration)
                : image.TryCreateCompletedRasterPlacementClone(_targetContext, out privateImage,
                    out expectedSourceLease, out svgMetadataFence);
            if (!created || privateImage is null || (!isIdle && expectedSourceLease is null))
            {
                return false;
            }

            _fences.Add(new RasterPlacementSourceFence(
                image, expectedSourceLease, svgMetadataFence, isIdle, idleGeneration));
            clone = privateImage;
            privateImage = null;
            return true;
        }
        finally
        {
            if (privateImage is not null)
                _targetContext.DiscardConstructionBlock(privateImage);
        }
    }

    private bool TryCloneInline(InlineContainerBox inline, out BlockBox? clone)
    {
        clone = null;
        InlineContainerBox? leaf = null;
        try
        {
            float inlineWidth = Math.Max(1f, (float)inline.Bounds.Width);
            long inlineStyleKeyCopyEntries;
            bool countsValid = _allowMeasuredVectorPlacement
                ? inline.TryGetPrivateRasterPlacementCountsWithMeasuredVectorPlacement(
                    _targetContext, inlineWidth, out _, out _, out inlineStyleKeyCopyEntries)
                : inline.TryGetPrivateRasterPlacementCounts(
                    _targetContext, inlineWidth, out _, out _, out inlineStyleKeyCopyEntries,
                    _allowUnmeasuredTextOnly, _allowIdleImagePlacements,
                    allowCodeForegroundSpans: _codeSources is not null && inline.ElementKey == MarkdownElementKeys.CodeBlock);
            if (!countsValid ||
                !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(
                    ref _styleKeyCopyEntries, inlineStyleKeyCopyEntries))
                return false;

            RasterPlacementSourceFence[]? leafFences;
            bool cloneCreated = _allowMeasuredVectorPlacement
                ? inline.TryCreatePrivateInlinePlacementCloneWithMeasuredVectorPlacement(
                    _targetContext, inlineWidth, out leaf, out leafFences)
                : inline.TryCreatePrivateInlinePlacementClone(
                    _targetContext, inlineWidth, out leaf, out leafFences,
                    _allowUnmeasuredTextOnly, _allowIdleImagePlacements,
                    allowCodeForegroundSpans: _codeSources is not null && inline.ElementKey == MarkdownElementKeys.CodeBlock);
            if (!cloneCreated || leaf is null || leafFences is null)
                return false;

            _fences.AddRange(leafFences);
            clone = leaf;
            leaf = null;
            return true;
        }
        finally
        {
            if (leaf is not null)
                _targetContext.DiscardConstructionBlock(leaf);
        }
    }

    private bool TryCloneCode(
        CodeBlockBox code,
        List<CodeBlockPlacementSourceFence> codeSources,
        int depth,
        out BlockBox? clone)
    {
        clone = null;
        if (!code.TryGetPrivatePlacementCounts(_targetContext, _allowUnmeasuredTextOnly,
                out int lineCount, out long codeStyleKeys) ||
            lineCount > PrivateRasterPlacementTree.MaximumNodes - _clonedNodes ||
            !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(ref _styleKeyCopyEntries, codeStyleKeys))
            return false;
        _clonedNodes += lineCount;
        long revision = code.PrivatePlacementRevision;
        CodeBlockBox? owner = code.CreatePrivatePlacementCloneShell(_targetContext);
        try
        {
            foreach (InlineContainerBox chunk in code.Chunks)
            {
                BlockBox? chunkClone = null;
                try
                {
                    if (!TryCloneNode(chunk, depth + 1, out chunkClone) ||
                        chunkClone is not InlineContainerBox privateChunk)
                        return false;
                    owner.AddPrivatePlacementChunk(privateChunk);
                    chunkClone = null;
                }
                finally
                {
                    if (chunkClone is not null)
                        _targetContext.DiscardConstructionBlock(chunkClone);
                }
            }
            if (!code.IsCurrentPrivatePlacement(revision))
                return false;
            codeSources.Add(new(code, owner, revision));
            clone = owner;
            owner = null;
            return true;
        }
        finally
        {
            if (owner is not null)
                _targetContext.DiscardConstructionBlock(owner);
        }
    }

    private bool TryCloneStack(StackBox stack, int depth, out BlockBox? clone)
    {
        clone = null;
        StackBox? owner = stack.CreatePrivateRasterPlacementCloneShell();
        try
        {
            foreach (BlockBox child in stack.Children)
            {
                _targetContext.CancellationToken.ThrowIfCancellationRequested();
                BlockBox? childClone = null;
                try
                {
                    if (!TryCloneNode(child, depth + 1, out childClone) ||
                        childClone is null)
                        return false;
                    owner.Add(childClone);
                    childClone = null;
                }
                finally
                {
                    if (childClone is not null)
                        _targetContext.DiscardConstructionBlock(childClone);
                }
            }

            clone = owner;
            owner = null;
            return true;
        }
        finally
        {
            if (owner is not null)
                _targetContext.DiscardConstructionBlock(owner);
        }
    }

    private bool TryCloneListItem(ListItemBox listItem, int depth, out BlockBox? clone)
    {
        clone = null;
        InlineContainerBox? markerClone = null;
        StackBox? contentClone = null;
        ListItemBox? owner = null;
        try
        {
            if (!TryCloneNode(listItem.Marker, depth + 1, out BlockBox? marker) ||
                marker is not InlineContainerBox markerInline)
            {
                if (marker is not null)
                    _targetContext.DiscardConstructionBlock(marker);
                return false;
            }
            markerClone = markerInline;

            if (!TryCloneNode(listItem.Content, depth + 1, out BlockBox? content) ||
                content is not StackBox contentStack)
            {
                if (content is not null)
                    _targetContext.DiscardConstructionBlock(content);
                return false;
            }
            contentClone = contentStack;

            owner = new ListItemBox(markerClone, contentClone, listItem.MarkerWidth)
            {
                BlockIndex = listItem.BlockIndex,
                FlowDirection = listItem.FlowDirection,
            };
            owner.CopyPlacementMarginsFrom(listItem);
            markerClone = null;
            contentClone = null;

            clone = owner;
            owner = null;
            return true;
        }
        finally
        {
            if (owner is not null)
                _targetContext.DiscardConstructionBlock(owner);
            else
            {
                if (contentClone is not null)
                    _targetContext.DiscardConstructionBlock(contentClone);
                if (markerClone is not null)
                    _targetContext.DiscardConstructionBlock(markerClone);
            }
        }
    }

    private bool TryCloneTable(TableBox table, int depth, out BlockBox? clone)
    {
        clone = null;
        var cellClones = new Dictionary<InlineContainerBox, InlineContainerBox>(
            ReferenceEqualityComparer.Instance);
        var ownerByFactory = false;
        try
        {
            foreach (InlineContainerBox cell in table.GetCellBoxes())
            {
                _targetContext.CancellationToken.ThrowIfCancellationRequested();
                if (!TryCloneNode(cell, depth + 1, out BlockBox? cellClone) ||
                    cellClone is not InlineContainerBox cellInline)
                {
                    if (cellClone is not null)
                        _targetContext.DiscardConstructionBlock(cellClone);
                    return false;
                }

                try
                {
                    cellClones.Add(cell, cellInline);
                }
                catch
                {
                    _targetContext.DiscardConstructionBlock(cellInline);
                    throw;
                }
            }

            if (!table.TryCreatePrivateRasterPlacementClone(
                    _targetContext, cellClones, out TableBox? privateTable) ||
                privateTable is null)
            {
                if (privateTable is not null)
                    _targetContext.DiscardConstructionBlock(privateTable);
                return false;
            }

            clone = privateTable;
            ownerByFactory = true;
            return true;
        }
        finally
        {
            if (!ownerByFactory)
            {
                foreach (InlineContainerBox cellClone in cellClones.Values)
                    _targetContext.DiscardConstructionBlock(cellClone);
            }
        }
    }
}
