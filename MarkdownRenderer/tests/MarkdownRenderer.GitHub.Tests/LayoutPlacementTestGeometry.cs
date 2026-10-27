using System.Collections.Generic;
using System.Linq;
using MarkdownRenderer.Layout;
using Windows.Foundation;

namespace MarkdownRenderer.GitHub.Tests;

internal static class LayoutPlacementTestGeometry
{
    internal static Rect ToLogical(LayoutSnapshot snapshot, BlockBox block)
        => snapshot.ToLogicalRectForBlockIndex(block.BlockIndex, block.Bounds);

    internal static Rect[] ToLogical(LayoutSnapshot snapshot, IEnumerable<BlockBox> blocks)
        => blocks.Select(block => ToLogical(snapshot, block)).ToArray();

    internal static Rect[] ToLogical(
        LayoutSnapshot snapshot,
        int blockIndex,
        IEnumerable<Rect> rectangles)
    {
        var map = snapshot.CaptureLogicalRectMapper();
        return rectangles.Select(rectangle => map(blockIndex, rectangle)).ToArray();
    }
}
