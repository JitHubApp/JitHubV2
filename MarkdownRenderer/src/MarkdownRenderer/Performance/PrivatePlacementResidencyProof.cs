using System;
using System.Collections.Generic;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Layout.Internal.Tables;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Decides from immutable construction shape whether a newly built tree can
/// skip a vector-scene residency policy (see
/// <see cref="MarkdownVectorSceneResidencyPolicy.CanOmitForPrivatePlacement"/>).
/// One instance is one bounded depth-first walk; it carries the walk's
/// budgets and the identities it has already admitted.
/// </summary>
internal sealed class PrivatePlacementResidencyProof
{
    private readonly Stack<(BlockBox Box, int Depth)> _pending;
    private readonly HashSet<BlockBox> _visited = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _selectableBlockIndices = new();
    private readonly HashSet<ImageBox> _images = new(ReferenceEqualityComparer.Instance);
    private MarkdownLayoutContext? _sourceContext;
    private int _visitedCount;
    private int _runCount;
    private int _imageCount;
    private long _textLength;
    private long _styleKeyCopyEntries;

    private PrivatePlacementResidencyProof(int pendingCapacity)
    {
        _pending = new Stack<(BlockBox Box, int Depth)>(pendingCapacity);
    }

    /// <summary>
    /// Proves that a small, known subset of a newly built tree cannot contain
    /// deferred vector-scene residency state. This runs before a snapshot or
    /// its layout gate exists, so it reads only immutable construction shape.
    /// Unknown or over-bound trees retain the normal policy.
    /// </summary>
    internal static bool CanOmitResidencyPolicy(IReadOnlyList<BlockBox> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count > PrivateRasterPlacementTree.MaximumRoots)
            return false;

        var proof = new PrivatePlacementResidencyProof(
            Math.Min(blocks.Count, PrivateRasterPlacementTree.MaximumNodes));
        return proof.TryPushRoots(blocks) && proof.Walk();
    }

    /// <summary>Nodes the walk may still visit or queue.</summary>
    private int RemainingNodes => PrivateRasterPlacementTree.MaximumNodes - _visitedCount - _pending.Count;

    private bool TryPushRoots(IReadOnlyList<BlockBox> blocks)
    {
        for (int rootIndex = blocks.Count - 1; rootIndex >= 0; rootIndex--)
        {
            BlockBox? root = blocks[rootIndex];
            if (root is null || _pending.Count >= PrivateRasterPlacementTree.MaximumNodes)
                return false;
            _pending.Push((root, 0));
        }

        return true;
    }

    private bool Walk()
    {
        while (_pending.Count > 0)
        {
            (BlockBox box, int depth) = _pending.Pop();
            if (depth > PrivateRasterPlacementTree.MaximumDepth ||
                ++_visitedCount > PrivateRasterPlacementTree.MaximumNodes ||
                !_visited.Add(box))
            {
                return false;
            }

            if (!TryVisit(box, depth))
                return false;
        }

        return true;
    }

    private bool TryVisit(BlockBox box, int depth) => box switch
    {
        ImageBox image => TryVisitImage(image),
        InlineContainerBox inline => TryVisitInline(inline),
        CodeBlockBox code when code.SafeHtmlInlineFlowScopeId is null &&
                               (_sourceContext is null || ReferenceEquals(_sourceContext, code.Context))
            => TryVisitCode(code, depth),
        StackBox stack when stack.GetType() == typeof(StackBox) &&
                            stack.IsPrivateRasterPlacementCloneEligible &&
                            stack.SafeHtmlInlineFlowScopeId is null
            => TryPushStackChildren(stack, depth),
        ListItemBox listItem when listItem.GetType() == typeof(ListItemBox) &&
                                  listItem.SafeHtmlInlineFlowScopeId is null
            => TryPushListItem(listItem, depth),
        TableBox table when table.GetType() == typeof(TableBox) &&
                            table.SafeHtmlInlineFlowScopeId is null &&
                            (_sourceContext is null || ReferenceEquals(_sourceContext, table.Context))
            => TryPushTableCells(table, depth),
        _ => false,
    };

    private bool TryVisitImage(ImageBox image)
    {
        if (image.BlockIndex <= 0 ||
            image.Context is null ||
            !_selectableBlockIndices.Add(image.BlockIndex) ||
            (_sourceContext is not null && !ReferenceEquals(_sourceContext, image.Context)) ||
            _imageCount >= InlineContainerBox.MaximumPrivateRasterPlacementImages ||
            !_images.Add(image))
        {
            return false;
        }

        _sourceContext ??= image.Context;
        _imageCount++;
        return true;
    }

    private bool TryVisitInline(InlineContainerBox inline)
    {
        if (inline.GetType() != typeof(InlineContainerBox) ||
            !PrivateRasterPlacementTree.CaptureState.IsSupportedInlineElement(inline.ElementKey) ||
            inline.SafeHtmlInlineFlowScopeId is not null ||
            inline.HasInlineEmbeds || inline.HasDeferredSceneMarker ||
            inline.VectorSceneRunCount != 0 ||
            !_selectableBlockIndices.Add(inline.BlockIndex) ||
            (_sourceContext is not null && !ReferenceEquals(_sourceContext, inline.Context)) ||
            !inline.TryGetPrivateRasterPlacementStyleKeyCopyEntries(
                out long inlineStyleKeyCopyEntries) ||
            !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(
                ref _styleKeyCopyEntries, inlineStyleKeyCopyEntries))
        {
            return false;
        }

        _sourceContext ??= inline.Context;
        IReadOnlyList<InlineRun> runs = inline.Runs;
        if (runs.Count > InlineContainerBox.MaximumPrivateRasterPlacementRuns - _runCount)
            return false;

        if (!TryAdmitInlineRuns(runs, out int inlineImageCount, out long inlineTextLength))
            return false;

        if (inlineImageCount != inline.InlineImageRuns.Count ||
            inlineTextLength != inline.PrivateRasterPlacementTextLength ||
            inlineTextLength > InlineContainerBox.MaximumPrivateRasterPlacementTextLength - _textLength)
        {
            return false;
        }

        _textLength += inlineTextLength;
        int foregroundSpanCount = inline.PrivateRasterPlacementForegroundSpanCount;
        if (foregroundSpanCount < 0 ||
            foregroundSpanCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns - runs.Count ||
            runs.Count + foregroundSpanCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns - _runCount)
            return false;
        _runCount += runs.Count + foregroundSpanCount;
        return true;
    }

    /// <summary>
    /// Admits one inline container's runs: closed links, supported text runs
    /// and not-yet-seen inline images, within the per-inline text budget.
    /// </summary>
    private bool TryAdmitInlineRuns(
        IReadOnlyList<InlineRun> runs,
        out int inlineImageCount,
        out long inlineTextLength)
    {
        inlineImageCount = 0;
        inlineTextLength = 0;
        for (int index = 0; index < runs.Count; index++)
        {
            InlineRun run = runs[index];
            switch (run)
            {
                case LinkRun { IsDisclosure: false, IsExpanded: false }:
                    break;
                case InlineImageRun imageRun:
                    inlineImageCount++;
                    if (_imageCount >= InlineContainerBox.MaximumPrivateRasterPlacementImages ||
                        !_images.Add(imageRun.Image))
                    {
                        return false;
                    }
                    _imageCount++;
                    break;
                default:
                    if (!PrivateTextRunPlacement.IsSupported(run))
                        return false;
                    break;
            }

            inlineTextLength += run.TextLength;
            if (inlineTextLength > InlineContainerBox.MaximumPrivateRasterPlacementTextLength)
                return false;
        }

        return true;
    }

    private bool TryVisitCode(CodeBlockBox code, int depth)
    {
        if (code.BlockIndex <= 0 || !_selectableBlockIndices.Add(code.BlockIndex) ||
            !code.TryGetPrivatePlacementCounts(code.Context, allowUnmeasured: true,
                out int lineCount, out long codeStyleKeys) ||
            lineCount > RemainingNodes ||
            !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(
                ref _styleKeyCopyEntries, codeStyleKeys))
            return false;
        _sourceContext ??= code.Context;
        _visitedCount += lineCount;
        if (code.Chunks.Count > RemainingNodes)
            return false;
        for (int index = code.Chunks.Count - 1; index >= 0; index--)
            _pending.Push((code.Chunks[index], depth + 1));
        return true;
    }

    private bool TryPushStackChildren(StackBox stack, int depth)
    {
        IReadOnlyList<BlockBox> children = stack.Children;
        if (children.Count > RemainingNodes)
            return false;
        for (int index = children.Count - 1; index >= 0; index--)
            _pending.Push((children[index], depth + 1));
        return true;
    }

    private bool TryPushListItem(ListItemBox listItem, int depth)
    {
        if (2 > RemainingNodes)
            return false;
        _pending.Push((listItem.Content, depth + 1));
        _pending.Push((listItem.Marker, depth + 1));
        return true;
    }

    private bool TryPushTableCells(TableBox table, int depth)
    {
        _sourceContext ??= table.Context;
        int remainingNodes = RemainingNodes;
        foreach (InlineContainerBox cell in table.GetCellBoxes())
        {
            if (remainingNodes-- <= 0)
                return false;
            _pending.Push((cell, depth + 1));
        }
        return true;
    }
}
