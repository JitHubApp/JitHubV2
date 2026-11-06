using System;
using System.Collections.Generic;
using Windows.Foundation;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Reads a snapshot's top-level blocks into placement inputs for a new
/// placement generation: copies a preserved prefix from the source
/// generation, reads arranged bounds for measured roots, and estimates the
/// height of roots that lazy layout has not measured yet.
/// </summary>
internal static class LayoutPlacementInputCapture
{
    /// <summary>
    /// Captures one input per block. When <paramref name="prefixSource"/> is
    /// set, ordinals before <paramref name="start"/> keep its placements.
    /// </summary>
    internal static LayoutPlacementInput[] Capture(
        IReadOnlyList<BlockBox> blocks,
        bool[]? measuredTopLevelBlocks,
        SharedLayoutMetrics? sharedLayoutMetrics,
        LayoutPlacementGeneration? prefixSource,
        int start)
    {
        var inputs = new LayoutPlacementInput[blocks.Count];
        for (int ordinal = 0; ordinal < blocks.Count; ordinal++)
        {
            if (prefixSource is not null && ordinal < start)
            {
                LayoutRootPlacement prior = prefixSource.GetPlacement(ordinal);
                inputs[ordinal] = new LayoutPlacementInput(
                    prior.BaseX,
                    prior.BaseTop,
                    prior.X,
                    prior.Width,
                    prior.Height,
                    prior.Measured,
                    prefixSource.SharesFlowWithNext(ordinal));
                continue;
            }

            BlockBox block = blocks[ordinal];
            Rect bounds = block.Bounds;
            bool measured = measuredTopLevelBlocks is null || measuredTopLevelBlocks[ordinal];
            bool sharesFlowWithNext = ordinal < blocks.Count - 1 &&
                StackBox.SharesSafeHtmlInlineFlow(block, blocks[ordinal + 1]);
            inputs[ordinal] = new LayoutPlacementInput(
                (float)bounds.X,
                (float)bounds.Y,
                (float)bounds.X,
                (float)bounds.Width,
                measured ? (float)bounds.Height : EstimateHeight(sharedLayoutMetrics, block, ordinal),
                measured,
                sharesFlowWithNext);
        }

        return inputs;
    }

    internal static float EstimateHeight(
        SharedLayoutMetrics? sharedLayoutMetrics,
        BlockBox block,
        int ordinal)
    {
        if (sharedLayoutMetrics?.TryGetHeight(ordinal, out float sharedHeight) == true)
            return sharedHeight;

        float margin = (float)(block.Margin.Top + block.Margin.Bottom);
        return block switch
        {
            ImageBox => Math.Max(160f, margin + 120f),
            EmbedBox => Math.Max(64f, margin + 48f),
            CodeBlockBox => Math.Max(72f, margin + 64f),
            TableBox table => Math.Clamp(36f + table.RowCount * 34f + margin, 72f, 360f),
            ListItemBox => Math.Max(36f, margin + 32f),
            StackBox stack => Math.Clamp(32f + stack.Children.Count * 28f + margin, 48f, 420f),
            ThematicBreakBox => Math.Max(16f, margin + 1f),
            InlineContainerBox => Math.Max(28f, margin + 24f),
            _ => Math.Max(32f, margin + 28f),
        };
    }
}
