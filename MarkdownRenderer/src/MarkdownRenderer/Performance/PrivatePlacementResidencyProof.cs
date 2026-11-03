using System;
using System.Collections.Generic;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Decides from immutable construction shape whether a newly built tree can
/// skip a vector-scene residency policy (see
/// <see cref="MarkdownVectorSceneResidencyPolicy.CanOmitForPrivatePlacement"/>).
/// </summary>
internal static class PrivatePlacementResidencyProof
{
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

        var pending = new Stack<(BlockBox Box, int Depth)>(
            Math.Min(blocks.Count, PrivateRasterPlacementTree.MaximumNodes));
        var visited = new HashSet<BlockBox>(ReferenceEqualityComparer.Instance);
        var selectableBlockIndices = new HashSet<int>();
        var images = new HashSet<ImageBox>(ReferenceEqualityComparer.Instance);
        MarkdownLayoutContext? sourceContext = null;
        int visitedCount = 0;
        int runCount = 0;
        int imageCount = 0;
        long textLength = 0;
        long styleKeyCopyEntries = 0;

        for (int rootIndex = blocks.Count - 1; rootIndex >= 0; rootIndex--)
        {
            BlockBox? root = blocks[rootIndex];
            if (root is null || pending.Count >= PrivateRasterPlacementTree.MaximumNodes)
                return false;
            pending.Push((root, 0));
        }

        while (pending.Count > 0)
        {
            (BlockBox box, int depth) = pending.Pop();
            if (depth > PrivateRasterPlacementTree.MaximumDepth ||
                ++visitedCount > PrivateRasterPlacementTree.MaximumNodes ||
                !visited.Add(box))
            {
                return false;
            }

            switch (box)
            {
                case ImageBox image:
                    if (image.BlockIndex <= 0 ||
                        image.Context is null ||
                        !selectableBlockIndices.Add(image.BlockIndex) ||
                        (sourceContext is not null && !ReferenceEquals(sourceContext, image.Context)) ||
                        imageCount >= InlineContainerBox.MaximumPrivateRasterPlacementImages ||
                        !images.Add(image))
                    {
                        return false;
                    }

                    sourceContext ??= image.Context;
                    imageCount++;
                    break;

                case InlineContainerBox inline:
                {
                    if (inline.GetType() != typeof(InlineContainerBox) ||
                        !PrivateRasterPlacementTree.CaptureState.IsSupportedInlineElement(inline.ElementKey) ||
                        inline.SafeHtmlInlineFlowScopeId is not null ||
                        inline.HasInlineEmbeds || inline.HasDeferredSceneMarker ||
                        inline.VectorSceneRunCount != 0 ||
                        !selectableBlockIndices.Add(inline.BlockIndex) ||
                        (sourceContext is not null && !ReferenceEquals(sourceContext, inline.Context)) ||
                        !inline.TryGetPrivateRasterPlacementStyleKeyCopyEntries(
                            out long inlineStyleKeyCopyEntries) ||
                        !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(
                            ref styleKeyCopyEntries, inlineStyleKeyCopyEntries))
                    {
                        return false;
                    }

                    sourceContext ??= inline.Context;
                    IReadOnlyList<InlineRun> runs = inline.Runs;
                    if (runs.Count > InlineContainerBox.MaximumPrivateRasterPlacementRuns - runCount)
                        return false;

                    int inlineImageCount = 0;
                    long inlineTextLength = 0;
                    for (int index = 0; index < runs.Count; index++)
                    {
                        InlineRun run = runs[index];
                        switch (run)
                        {
                            case LinkRun { IsDisclosure: false, IsExpanded: false }:
                                break;
                            case InlineImageRun imageRun:
                                inlineImageCount++;
                                if (imageCount >= InlineContainerBox.MaximumPrivateRasterPlacementImages ||
                                    !images.Add(imageRun.Image))
                                {
                                    return false;
                                }
                                imageCount++;
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

                    if (inlineImageCount != inline.InlineImageRuns.Count ||
                        inlineTextLength != inline.PrivateRasterPlacementTextLength ||
                        inlineTextLength > InlineContainerBox.MaximumPrivateRasterPlacementTextLength - textLength)
                    {
                        return false;
                    }

                    textLength += inlineTextLength;
                    int foregroundSpanCount = inline.PrivateRasterPlacementForegroundSpanCount;
                    if (foregroundSpanCount < 0 ||
                        foregroundSpanCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns - runs.Count ||
                        runs.Count + foregroundSpanCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns - runCount)
                        return false;
                    runCount += runs.Count + foregroundSpanCount;
                    break;
                }

                case CodeBlockBox code when code.SafeHtmlInlineFlowScopeId is null &&
                                           (sourceContext is null || ReferenceEquals(sourceContext, code.Context)):
                {
                    if (code.BlockIndex <= 0 || !selectableBlockIndices.Add(code.BlockIndex) ||
                        !code.TryGetPrivatePlacementCounts(code.Context, allowUnmeasured: true,
                            out int lineCount, out long codeStyleKeys) ||
                        lineCount > PrivateRasterPlacementTree.MaximumNodes - visitedCount - pending.Count ||
                        !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(
                            ref styleKeyCopyEntries, codeStyleKeys))
                        return false;
                    sourceContext ??= code.Context;
                    visitedCount += lineCount;
                    if (code.Chunks.Count > PrivateRasterPlacementTree.MaximumNodes - visitedCount - pending.Count)
                        return false;
                    for (int index = code.Chunks.Count - 1; index >= 0; index--)
                        pending.Push((code.Chunks[index], depth + 1));
                    break;
                }

                case StackBox stack when stack.GetType() == typeof(StackBox) &&
                                         stack.IsPrivateRasterPlacementCloneEligible &&
                                         stack.SafeHtmlInlineFlowScopeId is null:
                {
                    IReadOnlyList<BlockBox> children = stack.Children;
                    if (children.Count > PrivateRasterPlacementTree.MaximumNodes - visitedCount - pending.Count)
                        return false;
                    for (int index = children.Count - 1; index >= 0; index--)
                        pending.Push((children[index], depth + 1));
                    break;
                }

                case ListItemBox listItem when listItem.GetType() == typeof(ListItemBox) &&
                                               listItem.SafeHtmlInlineFlowScopeId is null:
                    if (2 > PrivateRasterPlacementTree.MaximumNodes - visitedCount - pending.Count)
                        return false;
                    pending.Push((listItem.Content, depth + 1));
                    pending.Push((listItem.Marker, depth + 1));
                    break;

                case TableBox table when table.GetType() == typeof(TableBox) &&
                                         table.SafeHtmlInlineFlowScopeId is null &&
                                         (sourceContext is null || ReferenceEquals(sourceContext, table.Context)):
                {
                    sourceContext ??= table.Context;
                    int remainingNodes = PrivateRasterPlacementTree.MaximumNodes - visitedCount - pending.Count;
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                    {
                        if (remainingNodes-- <= 0)
                            return false;
                        pending.Push((cell, depth + 1));
                    }
                    break;
                }

                default:
                    return false;
            }
        }

        return true;
    }
}
