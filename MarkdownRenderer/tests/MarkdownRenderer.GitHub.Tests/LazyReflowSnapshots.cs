using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Builds unmeasured (lazy) and eagerly arranged snapshots over probe roots
/// for <see cref="LayoutSnapshotLazyReflowTests"/>.
/// </summary>
internal static class LazyReflowSnapshots
{
    public const float DefaultWidth = 180f;

    public static LayoutSnapshot CreateUnmeasuredSnapshot(
        LazyReflowProbeBlock[] blocks,
        float width = DefaultWidth,
        Thickness padding = default,
        double spacing = 0,
        SharedLayoutMetrics? sharedLayoutMetrics = null)
    {
        for (int index = 0; index < blocks.Length; index++)
            blocks[index].BlockIndex = index;

        return new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            width,
            height: 0,
            sharedLayoutMetrics: sharedLayoutMetrics,
            documentPadding: padding,
            blockSpacing: spacing);
    }

    public static LayoutSnapshot CreateEagerSnapshot(
        LazyReflowProbeBlock[] blocks,
        float width,
        Thickness padding,
        double spacing)
    {
        float contentWidth = (float)System.Math.Max(1, width - padding.Left - padding.Right);
        float x = (float)padding.Left;
        float y = (float)padding.Top;
        float blockSpacing = (float)spacing;
        for (int ordinal = 0; ordinal < blocks.Length; ordinal++)
        {
            LazyReflowProbeBlock block = blocks[ordinal];
            block.Measure(contentWidth);
            block.Arrange(x, y, contentWidth);
            y += (float)block.Bounds.Height;
            if (ordinal < blocks.Length - 1 &&
                !StackBox.SharesSafeHtmlInlineFlow(blocks[ordinal], blocks[ordinal + 1]))
            {
                y += blockSpacing;
            }
        }

        y += (float)padding.Bottom;
        return new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            width,
            y,
            documentPadding: padding,
            blockSpacing: spacing);
    }

    public static LazyReflowProbeBlock[] CreateBlocks(float[] heights, int?[] flowScopes)
    {
        var blocks = new LazyReflowProbeBlock[heights.Length];
        for (int index = 0; index < heights.Length; index++)
        {
            blocks[index] = new LazyReflowProbeBlock(heights[index])
            {
                BlockIndex = index,
                SafeHtmlInlineFlowScopeId = flowScopes[index],
            };
        }

        return blocks;
    }
}
